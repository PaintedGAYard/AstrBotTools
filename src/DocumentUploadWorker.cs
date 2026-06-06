using System.Collections.Concurrent;
using System.Management.Automation;
using System.Text.Json;

namespace AstrBotTools;

/// <summary>
/// Uploads a single file to an AstrBot knowledge base and tracks its server-side vectorization progress.
/// All console output is accumulated in a thread-safe queue and drained on the pipeline thread
/// via <see cref="DrainOutput"/>.
/// </summary>
internal sealed class DocumentUploadWorker
{
    private readonly HttpClient _httpClient;
    private readonly string _filePath;
    private readonly UploadParameters _params;
    private readonly int _uploadRetryLimit;
    private readonly int _progressId;
    private readonly string _baseUrl;
    private readonly ConcurrentQueue<Action<IConsoleWriter>> _outputQueue = new();

    public DocumentUploadWorker(
        HttpClient httpClient,
        string baseUrl,
        string filePath,
        UploadParameters parameters,
        int uploadRetryLimit)
    {
        _httpClient = httpClient;
        _baseUrl = baseUrl.TrimEnd('/');
        _filePath = filePath;
        _params = parameters;
        _uploadRetryLimit = uploadRetryLimit;
        // Use a deterministic positive ActivityId from the file name hash
        _progressId = System.IO.Path.GetFileName(filePath).GetHashCode() & 0x7FFFFFFF;
    }

    /// <summary>
    /// Drains all pending output actions by invoking them on the given <see cref="IConsoleWriter"/>.
    /// Must be called from the pipeline thread.
    /// </summary>
    public void DrainOutput(IConsoleWriter console)
    {
        while (_outputQueue.TryDequeue(out var action))
            action(console);
    }

    /// <summary>
    /// Executes the upload retry loop. On success, transitions to server-side
    /// vectorization progress tracking using the same progress bar ActivityId.
    /// </summary>
    public async Task<UploadResult> ExecuteAsync(CancellationToken token)
    {
        var fileName = System.IO.Path.GetFileName(_filePath);
        var fileInfo = new FileInfo(_filePath);

        UploadResult? lastResult = null;

        for (int attempt = 1; attempt <= _uploadRetryLimit; attempt++)
        {
            token.ThrowIfCancellationRequested();

            Emit(w => w.WriteVerbose($"[{fileName}] Uploading... (attempt {attempt}/{_uploadRetryLimit})"));
            EmitProgress(attempt, "Uploading...");

            lastResult = await UploadOnceAsync(token);

            if (lastResult.Status is "Success" or "RetrySuccess")
            {
                Emit(w => w.WriteVerbose($"[{fileName}] ✅ Upload succeeded (task_id: {lastResult.TaskId})"));

                if (lastResult.TaskId != null)
                {
                    // Transition progress bar to vectorization phase
                    EmitProgressWaiting("Waiting for vectorization...");

                    var tracker = new VectorizationProgressTracker(
                        _httpClient, _baseUrl, lastResult.TaskId,
                        fileName, _progressId, _outputQueue);

                    var vecResult = await tracker.TrackAsync(token);

                    bool vecOk = vecResult.Status == "Completed";

                    return new UploadResult
                    {
                        FileName         = lastResult.FileName,
                        FilePath         = lastResult.FilePath,
                        FileSize         = lastResult.FileSize,
                        Status           = vecOk ? "Success" : $"Vectorize{vecResult.Status}",
                        AttemptNumber    = attempt,
                        TaskId           = lastResult.TaskId,
                        HttpStatusCode   = lastResult.HttpStatusCode,
                        ErrorMessage     = vecOk ? null : vecResult.ErrorMessage,
                        VectorizeStatus  = vecResult.Status,
                        DocId            = vecResult.DocId,
                        ChunkCount       = vecResult.ChunkCount,
                        Timestamp        = DateTime.UtcNow,
                    };
                }

                // Fallback: upload returned no task_id but HTTP status was success
                return new UploadResult
                {
                    FileName       = lastResult.FileName,
                    FilePath       = lastResult.FilePath,
                    FileSize       = lastResult.FileSize,
                    Status         = "Success",
                    AttemptNumber  = attempt,
                    TaskId         = lastResult.TaskId,
                    HttpStatusCode = lastResult.HttpStatusCode,
                    ErrorMessage   = lastResult.ErrorMessage,
                    Timestamp      = lastResult.Timestamp,
                };
            }

            if (attempt < _uploadRetryLimit)
            {
                Emit(w => w.WriteWarning(
                    $"[{fileName}] ❌ Attempt {attempt} failed: {lastResult.ErrorMessage}, retrying..."));
            }
        }

        // All retries exhausted
        Emit(w => w.WriteVerbose($"[{fileName}] ❌ All {_uploadRetryLimit} retries exhausted, upload failed"));
        EmitProgressComplete(false);

        return new UploadResult
        {
            FileName       = fileName,
            FilePath       = _filePath,
            FileSize       = fileInfo.Length,
            Status         = "Failed",
            AttemptNumber  = _uploadRetryLimit,
            TaskId         = lastResult?.TaskId,
            HttpStatusCode = lastResult?.HttpStatusCode,
            ErrorMessage   = lastResult?.ErrorMessage ?? $"Failed after {_uploadRetryLimit} retries",
            Timestamp      = DateTime.UtcNow,
        };
    }

    private async Task<UploadResult> UploadOnceAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        var fileName = System.IO.Path.GetFileName(_filePath);
        var fileInfo = new FileInfo(_filePath);
        var url = $"{_baseUrl}/api/kb/document/upload";

        byte[] fileBytes = await File.ReadAllBytesAsync(_filePath, token);

        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(fileBytes), "file0", fileName);
        content.Add(new StringContent(_params.KbId), "kb_id");
        content.Add(new StringContent(_params.ChunkSize   .ToString()), "chunk_size");
        content.Add(new StringContent(_params.ChunkOverlap.ToString()), "chunk_overlap");
        content.Add(new StringContent(_params.BatchSize   .ToString()), "batch_size");
        content.Add(new StringContent("1"), "tasks_limit");     // always 1 (per-file API)
        content.Add(new StringContent(_params.MaxRetries .ToString()), "max_retries");

        var response = await _httpClient.PostAsync(url, content, token);
        int statusCode = (int)response.StatusCode;
        var body = await response.Content.ReadAsStringAsync(token);

        string? taskId = null;
        string? apiMessage = null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("status", out var s) && s.GetString() == "ok" &&
                root.TryGetProperty("data", out var d) &&
                d.TryGetProperty("task_id", out var t))
                taskId = t.GetString();
            else if (root.TryGetProperty("message", out var m))
                apiMessage = m.GetString();
        }
        catch (JsonException) { }

        bool isSuccess = statusCode >= 200 && statusCode < 300 && taskId != null;

        return new UploadResult
        {
            FileName       = fileName,
            FilePath       = _filePath,
            FileSize       = fileInfo.Length,
            Status         = isSuccess ? "Success" : "Failed",
            AttemptNumber  = 1, // single-attempt value; final count is set by ExecuteAsync
            TaskId         = taskId,
            HttpStatusCode = statusCode,
            ErrorMessage   = isSuccess ? null : (apiMessage ?? body),
            Timestamp      = DateTime.UtcNow,
        };
    }

    private void Emit(Action<IConsoleWriter> action) => _outputQueue.Enqueue(action);

    private void EmitProgress(int attempt, string status)
    {
        var fileName = System.IO.Path.GetFileName(_filePath);
        var record = new ProgressRecord(
            _progressId,
            $"Upload: {fileName}",
            status)
        {
            CurrentOperation = $"Attempt {attempt}/{_uploadRetryLimit}",
            PercentComplete  = attempt > 1 ? (attempt - 1) * 100 / _uploadRetryLimit : 0,
        };
        Emit(w => w.WriteProgress(record));
    }

    private void EmitProgressWaiting(string status)
    {
        var fileName = System.IO.Path.GetFileName(_filePath);
        // Reuse same ActivityId; switch title from "Upload" to "Vectorize"
        var record = new ProgressRecord(
            _progressId,
            $"Vectorize: {fileName}",
            status)
        {
            CurrentOperation = "Queued",
            PercentComplete  = 0,
        };
        Emit(w => w.WriteProgress(record));
    }

    private void EmitProgressComplete(bool success)
    {
        var fileName = System.IO.Path.GetFileName(_filePath);
        var record = new ProgressRecord(
            _progressId,
            $"Upload: {fileName}",
            success ? "✅ Done" : "❌ Failed")
        {
            RecordType = ProgressRecordType.Completed,
        };
        Emit(w => w.WriteProgress(record));
    }
}
