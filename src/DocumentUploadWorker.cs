using System.Collections.Concurrent;
using System.Management.Automation;
using System.Text.Json;

namespace AstrBotTools;

/// <summary>
/// 单个文件的上传工作器。
/// 负责 HTTP 上传、内部重试循环、以及独立的进度条输出。
/// 所有控制台输出通过 <see cref="DrainOutput"/> 在 pipeline 线程上排空。
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
        // 用文件名 Hash 作为进度条 ActivityId，确保正数
        _progressId = System.IO.Path.GetFileName(filePath).GetHashCode() & 0x7FFFFFFF;
    }

    /// <summary>
    /// 在 pipeline 线程上排空所有待输出消息，并调用 IConsoleWriter 输出。
    /// </summary>
    public void DrainOutput(IConsoleWriter console)
    {
        while (_outputQueue.TryDequeue(out var action))
            action(console);
    }

    /// <summary>
    /// 执行上传 → 成功后自动追踪向量化进度。
    /// </summary>
    public async Task<UploadResult> ExecuteAsync(CancellationToken token)
    {
        var fileName = System.IO.Path.GetFileName(_filePath);
        var fileInfo = new FileInfo(_filePath);

        UploadResult? lastResult = null;

        for (int attempt = 1; attempt <= _uploadRetryLimit; attempt++)
        {
            token.ThrowIfCancellationRequested();

            Emit(w => w.WriteVerbose($"[{fileName}] 上传中... (第 {attempt}/{_uploadRetryLimit} 次)"));
            EmitProgress(attempt, "上传中...");

            lastResult = await UploadOnceAsync(token);

            if (lastResult.Status is "Success" or "RetrySuccess")
            {
                Emit(w => w.WriteVerbose($"[{fileName}] ✅ 上传成功 (task_id: {lastResult.TaskId})"));

                // 上传成功 → 追踪向量化进度（复用同一 ActivityId）
                string finalStatus;
                if (lastResult.TaskId != null)
                {
                    // 切换进度条标题为"向量化"
                    EmitProgressWaiting("等待向量化...");

                    var tracker = new VectorizationProgressTracker(
                        _httpClient, _baseUrl, lastResult.TaskId,
                        fileName, _progressId, _outputQueue);

                    var vecResult = await tracker.TrackAsync(token);

                    bool vecOk = vecResult.Status == "Completed";
                    finalStatus = vecOk ? "Success" : $"Vectorize{vecResult.Status}";

                    return new UploadResult
                    {
                        FileName         = lastResult.FileName,
                        FilePath         = lastResult.FilePath,
                        FileSize         = lastResult.FileSize,
                        Status           = finalStatus,
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

                // task_id 为 null 时返回原始成功结果
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
                    $"[{fileName}] ❌ 第 {attempt} 次上传失败: {lastResult.ErrorMessage}，即将重试..."));
            }
        }

        // 耗尽重试次数
        Emit(w => w.WriteVerbose($"[{fileName}] ❌ 已耗尽 {_uploadRetryLimit} 次重试，上传失败"));
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
            ErrorMessage   = lastResult?.ErrorMessage ?? $"重试 {_uploadRetryLimit} 次后仍然失败",
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
        content.Add(new StringContent("1"), "tasks_limit");     // 硬编码 1
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
            AttemptNumber  = 1, // 单次尝试，最终 AttemptNumber 由 ExecuteAsync 修正
            TaskId         = taskId,
            HttpStatusCode = statusCode,
            ErrorMessage   = isSuccess ? null : (apiMessage ?? body),
            Timestamp      = DateTime.UtcNow,
        };
    }

    private void Emit(Action<IConsoleWriter> action)
    {
        _outputQueue.Enqueue(action);
    }

    private void EmitProgress(int attempt, string status)
    {
        var fileName = System.IO.Path.GetFileName(_filePath);
        var record = new ProgressRecord(
            _progressId,
            $"上传: {fileName}",
            status)
        {
            CurrentOperation = $"尝试 {attempt}/{_uploadRetryLimit}",
            PercentComplete  = attempt > 1 ? (attempt - 1) * 100 / _uploadRetryLimit : 0,
        };
        Emit(w => w.WriteProgress(record));
    }

    private void EmitProgressWaiting(string status)
    {
        var fileName = System.IO.Path.GetFileName(_filePath);
        // 切换进度条标题到"向量化"，复用同一 ActivityId
        var record = new ProgressRecord(
            _progressId,
            $"向量化: {fileName}",
            status)
        {
            CurrentOperation = "排队中",
            PercentComplete  = 0,
        };
        Emit(w => w.WriteProgress(record));
    }

    private void EmitProgressComplete(bool success)
    {
        var fileName = System.IO.Path.GetFileName(_filePath);
        var record = new ProgressRecord(
            _progressId,
            $"上传: {fileName}",
            success ? "✅ 完成" : "❌ 失败")
        {
            RecordType = ProgressRecordType.Completed,
        };
        Emit(w => w.WriteProgress(record));
    }
}
