using System.Collections.Concurrent;
using System.Management.Automation;
using System.Text.Json;

namespace AstrBotTools;

/// <summary>
/// AstrBot 后台向量化处理进度追踪器。
/// 轮询 <c>GET /api/kb/document/upload/progress?task_id=</c> 接口，
/// 用同一 ActivityId 更新进度条（chunking → embedding → completed），
/// 所有输出通过共享队列在 pipeline 线程上排空。
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
    private static readonly TimeSpan GlobalTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PerRequestTimeout = TimeSpan.FromSeconds(30);

    public VectorizationProgressTracker(
        HttpClient httpClient,
        string baseUrl,
        string taskId,
        string fileName,
        int progressId,
        ConcurrentQueue<Action<IConsoleWriter>> outputQueue)
    {
        _httpClient = httpClient;
        _baseUrl = baseUrl.TrimEnd('/');
        _taskId = taskId;
        _fileName = fileName;
        _progressId = progressId;
        _outputQueue = outputQueue;
    }

    /// <summary>
    /// 开始轮询并返回向量化结果。最多轮询 10 分钟。
    /// </summary>
    public async Task<VectorizationResult> TrackAsync(CancellationToken token)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(GlobalTimeout);

        try
        {
            return await PollUntilCompleteAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            // 全局超时
            Emit(w => w.WriteWarning(
                $"[{_fileName}] ⏱ 向量化处理超时 ({GlobalTimeout.TotalMinutes} 分钟)"));
            EmitProgressComplete(false, "⏱ 超时");
            return new VectorizationResult
            {
                Status       = "Timeout",
                ErrorMessage = $"向量化处理 {GlobalTimeout.TotalMinutes} 分钟超时",
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
    /// 单次轮询。返回 null 表示还在处理中。
    /// </summary>
    private async Task<VectorizationResult?> PollOnceAsync(string url, CancellationToken ct)
    {
        // 每次请求独立 30 秒超时，避免挂住
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
            Emit(w => w.WriteVerbose($"[{_fileName}] ⚠ 进度查询超时，继续等待..."));
            return null; // 单次超时不终止，继续轮询
        }
        catch (HttpRequestException ex)
        {
            Emit(w => w.WriteVerbose($"[{_fileName}] ⚠ 进度查询失败: {ex.Message}"));
            return null; // 网络波动不终止
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
                    EmitProgressComplete(true, "✅ 完成");
                    return vecResult;

                default:
                    Emit(w => w.WriteVerbose(
                        $"[{_fileName}] ⚠ 未知状态: {taskStatus}"));
                    return null;
            }
        }
        catch (JsonException ex)
        {
            Emit(w => w.WriteVerbose($"[{_fileName}] ⚠ 进度响应解析失败: {ex.Message}"));
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
            "chunking"  => "切块",
            "embedding" => "嵌入",
            _           => stage ?? "处理",
        };

        var pct = total > 0 ? Math.Min(current * 100 / total, 100) : 0;

        var record = new ProgressRecord(
            _progressId,
            $"向量化: {_fileName}",
            $"{stageLabel}中... {current}/{total}")
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
                ErrorMessage = "响应中缺少 result 字段",
            };
        }

        var uploaded = result.TryGetProperty("uploaded", out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().ToList()
            : new List<JsonElement>();

        // 取第一个上传成功的文档
        var first = uploaded.FirstOrDefault();
        if (first.ValueKind == JsonValueKind.Undefined)
        {
            return new VectorizationResult
            {
                Status       = "Failed",
                ErrorMessage = "服务端未返回已上传的文档信息",
            };
        }

        return new VectorizationResult
        {
            Status     = "Completed",
            DocId      = first.TryGetProperty("doc_id", out var d) ? d.GetString() : null,
            ChunkCount = first.TryGetProperty("chunk_count", out var c) ? c.GetInt32() : 0,
        };
    }

    // ========== 输出队列辅助 ==========

    private void Emit(Action<IConsoleWriter> action)
    {
        _outputQueue.Enqueue(action);
    }

    private void EmitProgressComplete(bool success, string label)
    {
        var record = new ProgressRecord(
            _progressId,
            $"向量化: {_fileName}",
            label)
        {
            RecordType = ProgressRecordType.Completed,
        };
        Emit(w => w.WriteProgress(record));
    }
}
