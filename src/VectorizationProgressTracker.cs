using System.Collections.Concurrent;
using System.Management.Automation;
using System.Text.Json;

namespace AstrBotTools;

/// <summary>
/// Polls the AstrBot server-side vectorization progress endpoint
/// (<c>GET /api/kb/document/upload/progress?task_id=</c>) and updates the
/// progress bar through the chunking → embedding → completed lifecycle.
/// All output is enqueued to a shared <see cref="ConcurrentQueue{T}"/> for
/// pipeline-thread draining.
/// </summary>
internal sealed class VectorizationProgressTracker
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly string _taskId;
    private readonly string _fileName;
    private readonly int _progressId;
    private readonly ConcurrentQueue<Action<IConsoleWriter>> _outputQueue;

    private const int PollIntervalMs = 2_000;
    private readonly TimeSpan _globalTimeout;
    private static readonly TimeSpan PerRequestTimeout = TimeSpan.FromSeconds(30);

    public VectorizationProgressTracker(
        HttpClient httpClient,
        string baseUrl,
        string taskId,
        string fileName,
        int progressId,
        ConcurrentQueue<Action<IConsoleWriter>> outputQueue,
        TimeSpan vectorizationTimeout)
    {
        _httpClient = httpClient;
        _baseUrl = baseUrl.TrimEnd('/');
        _taskId = taskId;
        _fileName = fileName;
        _progressId = progressId;
        _outputQueue = outputQueue;
        _globalTimeout = vectorizationTimeout;
    }

    /// <summary>
    /// Starts polling the progress endpoint and returns when vectorization completes,
    /// fails, or the global timeout is reached. If <see cref="_globalTimeout"/>
    /// is <see cref="TimeSpan.Zero"/> or negative, no timeout is applied (runs until
    /// completion or user cancellation).
    /// </summary>
    public async Task<VectorizationResult> TrackAsync(CancellationToken token)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(token);

        bool hasTimeout = _globalTimeout > TimeSpan.Zero;
        if (hasTimeout)
            cts.CancelAfter(_globalTimeout);

        try
        {
            return await PollUntilCompleteAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            Emit(w => w.WriteWarning(
                $"[{_fileName}] ⏱ Vectorization timed out ({_globalTimeout.TotalMinutes} min)"));
            EmitProgressComplete(false, "⏱ Timed out");
            return new VectorizationResult
            {
                Status       = "Timeout",
                ErrorMessage = $"Vectorization timed out after {_globalTimeout.TotalMinutes} minutes",
            };
        }
    }

    private async Task<VectorizationResult> PollUntilCompleteAsync(CancellationToken ct)
    {
        var url = $"{_baseUrl}/api/kb/document/upload/progress?task_id={_taskId}";

        while (!ct.IsCancellationRequested)
        {
            var result = await PollOnceAsync(url, ct);
            if (result != null)
                return result;

            await Task.Delay(PollIntervalMs, ct);
        }

        ct.ThrowIfCancellationRequested();
        return null!; // unreachable
    }

    /// <summary>
    /// Performs a single poll. Returns null if the task is still processing.
    /// </summary>
    private async Task<VectorizationResult?> PollOnceAsync(string url, CancellationToken ct)
    {
        using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        reqCts.CancelAfter(PerRequestTimeout);

        string body;
        try
        {
            var response = await _httpClient.GetAsync(url, reqCts.Token);
            response.EnsureSuccessStatusCode();
            body = await response.Content.ReadAsStringAsync(reqCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Emit(w => w.WriteVerbose($"[{_fileName}] ⚠ Progress query timed out, continuing..."));
            return null; // per-request timeout is non-fatal
        }
        catch (HttpRequestException ex)
        {
            Emit(w => w.WriteVerbose($"[{_fileName}] ⚠ Progress query failed: {ex.Message}"));
            return null; // transient network error is non-fatal
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (!root.TryGetProperty("status", out var s) || s.GetString() != "ok")
                return null;

            if (!root.TryGetProperty("data", out var data))
                return null;

            var taskStatus = data.TryGetProperty("status", out var ts)
                ? ts.GetString()
                : null;

            switch (taskStatus)
            {
                case "processing":
                    HandleProcessing(data);
                    return null;

                case "completed":
                    var vecResult = HandleCompleted(data);
                    EmitProgressComplete(true, "✅ Done");
                    return vecResult;

                default:
                    Emit(w => w.WriteVerbose(
                        $"[{_fileName}] ⚠ Unknown status: {taskStatus}"));
                    return null;
            }
        }
        catch (JsonException ex)
        {
            Emit(w => w.WriteVerbose($"[{_fileName}] ⚠ Failed to parse progress response: {ex.Message}"));
            return null;
        }
    }

    private void HandleProcessing(JsonElement data)
    {
        if (!data.TryGetProperty("progress", out var progress))
            return;

        var stage = progress.TryGetProperty("stage", out var st) ? st.GetString() : null;
        var current = progress.TryGetProperty("current", out var cur) ? cur.GetInt32() : 0;
        var total   = progress.TryGetProperty("total", out var tot) ? tot.GetInt32() : 0;

        var stageLabel = stage switch
        {
            "chunking"  => "Chunking",
            "embedding" => "Embedding",
            _           => stage ?? "Processing",
        };

        var pct = total > 0 ? Math.Min(current * 100 / total, 100) : 0;

        var record = new ProgressRecord(
            _progressId,
            $"Vectorize: {_fileName}",
            $"{stageLabel}... {current}/{total}")
        {
            CurrentOperation = stageLabel,
            PercentComplete  = pct,
        };
        Emit(w => w.WriteProgress(record));
    }

    private static VectorizationResult HandleCompleted(JsonElement data)
    {
        if (!data.TryGetProperty("result", out var result))
        {
            return new VectorizationResult
            {
                Status       = "Failed",
                ErrorMessage = "Response missing 'result' field",
            };
        }

        var uploaded = result.TryGetProperty("uploaded", out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().ToList()
            : new List<JsonElement>();

        var first = uploaded.FirstOrDefault();
        if (first.ValueKind == JsonValueKind.Undefined)
        {
            return new VectorizationResult
            {
                Status       = "Failed",
                ErrorMessage = "Server returned no uploaded documents",
            };
        }

        return new VectorizationResult
        {
            Status     = "Completed",
            DocId      = first.TryGetProperty("doc_id", out var d) ? d.GetString() : null,
            ChunkCount = first.TryGetProperty("chunk_count", out var c) ? c.GetInt32() : 0,
        };
    }

    private void Emit(Action<IConsoleWriter> action) => _outputQueue.Enqueue(action);

    private void EmitProgressComplete(bool success, string label)
    {
        var record = new ProgressRecord(
            _progressId,
            $"Vectorize: {_fileName}",
            label)
        {
            RecordType = ProgressRecordType.Completed,
        };
        Emit(w => w.WriteProgress(record));
    }
}
