using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AstrBotTools;

[Cmdlet(VerbsCommon.Add, "AstrBotKnowledgeBaseDocument",
        SupportsShouldProcess = true,
        DefaultParameterSetName = "Path")]
[Alias("Add-KBDoc")]
[OutputType(typeof(UploadResult))]
public sealed class AddAstrBotKnowledgeBaseDocument : PSCmdlet, IDisposable
{
    // ========== 核心参数 ==========

    [Parameter(Mandatory = true, ValueFromPipeline = true,
               ValueFromPipelineByPropertyName = true, Position = 0)]
    [Alias("PSPath", "FullName")]
    [ValidateNotNullOrEmpty]
    public string[] Path { get; set; } = Array.Empty<string>();

    [Parameter(Mandatory = true)]
    [ValidateNotNullOrEmpty]
    public string BaseUrl { get; set; } = string.Empty;

    [Parameter(Mandatory = true)]
    [ValidateNotNullOrEmpty]
    public string AuthToken { get; set; } = string.Empty;

    [Parameter(Mandatory = true)]
    [ValidateNotNullOrEmpty]
    public string KbId { get; set; } = string.Empty;

    // ========== 后端参数（传给 AstrBot API）==========

    [Parameter()] public int ChunkSize    { get; set; } = 512;
    [Parameter()] public int ChunkOverlap { get; set; } = 50;
    [Parameter()] public int BatchSize    { get; set; } = 32;
    [Parameter()] public int TasksLimit   { get; set; } = 3;
    [Parameter()] public int MaxRetries   { get; set; } = 3;

    // ========== 传输控制参数 ==========

    [Parameter()]
    [ValidateRange(1, 100)]
    public int UploadBatchSize { get; set; } = 10;

    [Parameter()]
    [ValidateRange(0, 20)]
    public int UploadRetryLimit { get; set; } = 3;

    // ========== 内部状态 ==========

    private HttpClient _httpClient = null!;
    private readonly Collection<Task<UploadResult?>> _pendingTasks = new();
    private readonly Collection<string> _pendingFilePaths = new();
    private readonly List<UploadResult> _allResults = new();
    private readonly List<string> _retryQueue = new();
    private int _filesSubmitted;
    private int _totalFiles;
    private CancellationTokenSource _cts = null!;

    // ========== 生命周期 ==========

    protected override void BeginProcessing()
    {
        _cts = new CancellationTokenSource();

        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = UploadBatchSize,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        };

        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(180)
        };
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", AuthToken);
    }

    protected override void ProcessRecord()
    {
        // 兼容数组输入和管道逐项输入
        foreach (var rawPath in Path)
        {
            var resolved = ResolveSingleFilePath(rawPath);
            if (resolved == null) continue;

            _totalFiles++;
            EnqueueUpload(resolved);
        }
    }

    protected override void EndProcessing()
    {
        try
        {
            // 1. 冲洗剩余的待处理任务
            FlushPendingBatch();

            // 2. 重试循环
            for (int round = 1; round <= UploadRetryLimit && _retryQueue.Count > 0; round++)
            {
                WriteVerbose($"第 {round}/{UploadRetryLimit} 轮重试，共 {_retryQueue.Count} 个文件");

                // 把剩余待重试的任务分批处理
                var filesThisRound = _retryQueue.ToArray();
                _retryQueue.Clear();

                foreach (var file in filesThisRound)
                {
                    if (_cts.IsCancellationRequested) break;
                    EnqueueUpload(file, isRetry: true);
                }
                FlushPendingBatch();
            }

            // 3. 剩余的重试耗尽 → 报错
            foreach (var exhaustedFile in _retryQueue)
            {
                var errResult = new UploadResult
                {
                    FileName   = System.IO.Path.GetFileName(exhaustedFile),
                    FilePath   = exhaustedFile,
                    Status     = "Failed",
                    ErrorMessage = $"重试 {UploadRetryLimit} 次后仍然失败",
                    Timestamp  = DateTime.UtcNow,
                };
                _allResults.Add(errResult);

                WriteError(new ErrorRecord(
                    new InvalidOperationException($"上传失败（已耗尽重试次数）: {exhaustedFile}"),
                    "UPLOAD_EXHAUSTED",
                    ErrorCategory.LimitsExceeded,
                    exhaustedFile));
            }

            // 4. 输出所有结果
            foreach (var result in _allResults)
            {
                WriteObject(result);
            }
        }
        finally
        {
            _httpClient?.Dispose();
            _cts?.Dispose();
        }
    }

    protected override void StopProcessing()
    {
        _cts?.Cancel();
        _httpClient?.Dispose();
    }

    // ========== 核心逻辑 ==========

    /// <summary>解析文件路径，不存在则 Warning 跳过</summary>
    private string? ResolveSingleFilePath(string raw)
    {
        // 尝试直接解析，也支持相对路径
        try
        {
            var resolved = SessionState.Path.GetResolvedPSPathFromPSPath(raw);
            var path = resolved[0].Path;
            if (System.IO.File.Exists(path) && !System.IO.Directory.Exists(path))
                return path;

            WriteWarning($"文件不存在或不是文件: {raw}");
            return null;
        }
        catch (ItemNotFoundException)
        {
            WriteWarning($"文件不存在: {raw}");
            return null;
        }
    }

    /// <summary>启动一个上传任务并加入待处理队列</summary>
    private void EnqueueUpload(string filePath, bool isRetry = false)
    {
        var capturedPath = filePath; // C# foreach 不需要快照，但显式捕获更安全
        var attemptBase = _retryQueue.Contains(capturedPath)
            ? UploadRetryLimit - _retryQueue.Count + 1  // 简化：已有重试次数
            : 0;

        // 记录实际尝试次数：非重试 = 第1次
        int attemptNumber = isRetry
            ? UploadRetryLimit - _retryQueue.Count + 2  // 略复杂，简化在 Flush 中处理
            : 1;

        var task = Task.Run(async () =>
        {
            try
            {
                return await UploadSingleFileAsync(capturedPath, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                return new UploadResult
                {
                    FileName   = System.IO.Path.GetFileName(capturedPath),
                    FilePath   = capturedPath,
                    Status     = "Failed",
                    ErrorMessage = ex.Message,
                    Timestamp  = DateTime.UtcNow,
                };
            }
        }, _cts.Token);

        _pendingTasks.Add(task);
        _pendingFilePaths.Add(capturedPath);
        _filesSubmitted++;

        // 满一批就等待
        if (_pendingTasks.Count >= UploadBatchSize)
        {
            FlushPendingBatch();
        }
    }

    /// <summary>等待当前批次完成，收集结果，分离成功/失败</summary>
    private void FlushPendingBatch()
    {
        if (_pendingTasks.Count == 0) return;

        WriteVerbose($"等待 {_pendingTasks.Count} 个上传任务完成...");

        try
        {
            Task.WaitAll(_pendingTasks.ToArray(), _cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C 中断
        }
        catch (AggregateException)
        {
            // 单个 task 的异常在 task.Result 中处理
        }

        for (int i = 0; i < _pendingTasks.Count; i++)
        {
            var task  = _pendingTasks[i];
            var fPath = _pendingFilePaths[i];

            // 提取结果
            UploadResult? result = null;
            if (task.IsCompletedSuccessfully)
            {
                result = task.Result;
            }
            else if (task.IsFaulted)
            {
                var ex = task.Exception?.InnerException ?? task.Exception;
                result = new UploadResult
                {
                    FileName   = System.IO.Path.GetFileName(fPath),
                    FilePath   = fPath,
                    Status     = "Failed",
                    ErrorMessage = ex?.Message ?? "未知错误",
                    Timestamp  = DateTime.UtcNow,
                };
            }
            else if (task.IsCanceled)
            {
                // 用户取消，不处理
                continue;
            }

            if (result == null) continue;

            // 判定成功/失败
            if (result.Status is "Success" or "RetrySuccess")
            {
                _allResults.Add(result);
            }
            else
            {
                _retryQueue.Add(result.FilePath);
            }
        }

        // 更新进度
        ReportProgress();

        _pendingTasks.Clear();
        _pendingFilePaths.Clear();
    }

    /// <summary>执行单个文件的上传（async 被 Task.Run 包裹）</summary>
    private async Task<UploadResult> UploadSingleFileAsync(string filePath,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        var fileName  = System.IO.Path.GetFileName(filePath);
        var fileInfo  = new FileInfo(filePath);
        var url       = $"{BaseUrl.TrimEnd('/')}/api/kb/document/upload";

        byte[] fileBytes = await File.ReadAllBytesAsync(filePath, token);

        using var content = new MultipartFormDataContent();

        // file0
        content.Add(new ByteArrayContent(fileBytes), "file0", fileName);
        // kb_id
        content.Add(new StringContent(KbId), "kb_id");
        // 分块设置
        content.Add(new StringContent(ChunkSize   .ToString()), "chunk_size");
        content.Add(new StringContent(ChunkOverlap.ToString()), "chunk_overlap");
        // 批处理设置
        content.Add(new StringContent(BatchSize   .ToString()), "batch_size");
        content.Add(new StringContent(TasksLimit  .ToString()), "tasks_limit");
        content.Add(new StringContent(MaxRetries  .ToString()), "max_retries");

        var response = await _httpClient.PostAsync(url, content, token);
        int statusCode = (int)response.StatusCode;
        var body = await response.Content.ReadAsStringAsync(token);

        // 解析响应
        string? taskId = null;
        string? apiMessage = null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("status", out var statusEl))
            {
                var apiStatus = statusEl.GetString();
                if (apiStatus == "ok" &&
                    root.TryGetProperty("data", out var dataEl) &&
                    dataEl.TryGetProperty("task_id", out var taskIdEl))
                {
                    taskId = taskIdEl.GetString();
                }
                else if (root.TryGetProperty("message", out var msgEl))
                {
                    apiMessage = msgEl.GetString();
                }
            }
        }
        catch (JsonException)
        {
            // 非 JSON 响应
        }

        bool isSuccess = statusCode >= 200 && statusCode < 300 && taskId != null;

        return new UploadResult
        {
            FileName       = fileName,
            FilePath       = filePath,
            FileSize       = fileInfo.Length,
            Status         = isSuccess ? "Success" : "Failed",
            AttemptNumber  = 1, // 由调用方修正
            TaskId         = taskId,
            HttpStatusCode = statusCode,
            ErrorMessage   = isSuccess ? null : (apiMessage ?? body),
            Timestamp      = DateTime.UtcNow,
        };
    }

    /// <summary>更新进度条</summary>
    private void ReportProgress()
    {
        int completed = _allResults.Count;
        int total     = Math.Max(_totalFiles, 1); // 避免除零

        var record = new ProgressRecord(0, "AstrBot 知识库上传", $"已完成 {completed} / {total} 个文件")
        {
            PercentComplete = Math.Min(completed * 100 / total, 100),
            CurrentOperation = $"待重试: {_retryQueue.Count} 个",
        };
        WriteProgress(record);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _httpClient?.Dispose();
        GC.SuppressFinalize(this);
    }
}
