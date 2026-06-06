using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AstrBotTools;

[Cmdlet(VerbsCommon.Add, "AstrBotKnowledgeBaseDocument",
        SupportsShouldProcess = true,
        DefaultParameterSetName = "Path")]
[Alias("Add-KBDoc")]
[OutputType(typeof(UploadResult))]
public sealed class AddAstrBotKnowledgeBaseDocument : PSCmdlet, IDisposable
{
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

    [Parameter()] public int ChunkSize    { get; set; } = 512;
    [Parameter()] public int ChunkOverlap { get; set; } = 50;
    [Parameter()] public int BatchSize    { get; set; } = 32;
    [Parameter()] public int TasksLimit   { get; set; } = 3;
    [Parameter()] public int MaxRetries   { get; set; } = 3;

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
    private int _totalFiles;
    private CancellationTokenSource _cts = null!;

    protected override void BeginProcessing()
    {
        _cts = new CancellationTokenSource();

        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = UploadBatchSize,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        };

        _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(180) };
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", AuthToken);
    }

    protected override void ProcessRecord()
    {
        foreach (var rawPath in Path)
        {
            Collection<PathInfo> resolvedList;
            try
            {
                resolvedList = SessionState.Path.GetResolvedPSPathFromPSPath(rawPath);
            }
            catch (ItemNotFoundException)
            {
                WriteWarning($"未找到匹配: {rawPath}");
                continue;
            }
            catch (PSNotSupportedException)
            {
                WriteWarning($"不支持该路径提供程序: {rawPath}");
                continue;
            }
            catch (Exception ex) when (ex is not PipelineStoppedException
                                            and not FlowControlException)
            {
                WriteWarning($"路径解析失败: {rawPath} — {ex.Message}");
                continue;
            }

            foreach (var resolved in resolvedList)
            {
                var providerPath = resolved.ProviderPath;
                if (System.IO.File.Exists(providerPath))
                {
                    _totalFiles++;
                    EnqueueUpload(providerPath);
                }
                else
                {
                    WriteWarning($"不是文件，跳过: {providerPath}");
                }
            }
        }
    }

    protected override void EndProcessing()
    {
        try
        {
            FlushPendingBatch();

            for (int round = 1; round <= UploadRetryLimit && _retryQueue.Count > 0; round++)
            {
                WriteVerbose($"第 {round}/{UploadRetryLimit} 轮重试，共 {_retryQueue.Count} 个文件");

                var filesThisRound = _retryQueue.ToArray();
                _retryQueue.Clear();

                foreach (var file in filesThisRound)
                {
                    if (_cts.IsCancellationRequested) break;
                    EnqueueUpload(file, isRetry: true);
                }
                FlushPendingBatch();
            }

            foreach (var exhaustedFile in _retryQueue)
            {
                _allResults.Add(new UploadResult
                {
                    FileName     = System.IO.Path.GetFileName(exhaustedFile),
                    FilePath     = exhaustedFile,
                    Status       = "Failed",
                    ErrorMessage = $"重试 {UploadRetryLimit} 次后仍然失败",
                    Timestamp    = DateTime.UtcNow,
                });

                WriteError(new ErrorRecord(
                    new InvalidOperationException($"上传失败（已耗尽重试次数）: {exhaustedFile}"),
                    "UPLOAD_EXHAUSTED",
                    ErrorCategory.LimitsExceeded,
                    exhaustedFile));
            }

            foreach (var result in _allResults)
                WriteObject(result);
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

    private void EnqueueUpload(string filePath, bool isRetry = false)
    {
        var task = Task.Run(async () =>
        {
            try
            {
                return await UploadSingleFileAsync(filePath, _cts.Token);
            }
            catch (OperationCanceledException) { return null; }
            catch (Exception ex)
            {
                return new UploadResult
                {
                    FileName     = System.IO.Path.GetFileName(filePath),
                    FilePath     = filePath,
                    Status       = "Failed",
                    ErrorMessage = ex.Message,
                    Timestamp    = DateTime.UtcNow,
                };
            }
        }, _cts.Token);

        _pendingTasks.Add(task);
        _pendingFilePaths.Add(filePath);

        if (_pendingTasks.Count >= UploadBatchSize)
            FlushPendingBatch();
    }

    private void FlushPendingBatch()
    {
        if (_pendingTasks.Count == 0) return;

        WriteVerbose($"等待 {_pendingTasks.Count} 个上传任务完成...");

        try { Task.WaitAll(_pendingTasks.ToArray(), _cts.Token); }
        catch (OperationCanceledException) { }
        catch (AggregateException) { }

        for (int i = 0; i < _pendingTasks.Count; i++)
        {
            var task  = _pendingTasks[i];
            var fPath = _pendingFilePaths[i];

            UploadResult? result = null;
            if (task.IsCompletedSuccessfully)
                result = task.Result;
            else if (task.IsFaulted)
            {
                var ex = task.Exception?.InnerException ?? task.Exception;
                result = new UploadResult
                {
                    FileName     = System.IO.Path.GetFileName(fPath),
                    FilePath     = fPath,
                    Status       = "Failed",
                    ErrorMessage = ex?.Message ?? "未知错误",
                    Timestamp    = DateTime.UtcNow,
                };
            }
            else continue; // canceled

            if (result.Status is "Success" or "RetrySuccess")
                _allResults.Add(result);
            else
                _retryQueue.Add(result.FilePath);
        }

        ReportProgress();
        _pendingTasks.Clear();
        _pendingFilePaths.Clear();
    }

    private async Task<UploadResult> UploadSingleFileAsync(string filePath,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        var fileName  = System.IO.Path.GetFileName(filePath);
        var fileInfo  = new FileInfo(filePath);
        var url       = $"{BaseUrl.TrimEnd('/')}/api/kb/document/upload";

        byte[] fileBytes = await File.ReadAllBytesAsync(filePath, token);

        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(fileBytes), "file0", fileName);
        content.Add(new StringContent(KbId), "kb_id");
        content.Add(new StringContent(ChunkSize   .ToString()), "chunk_size");
        content.Add(new StringContent(ChunkOverlap.ToString()), "chunk_overlap");
        content.Add(new StringContent(BatchSize   .ToString()), "batch_size");
        content.Add(new StringContent(TasksLimit  .ToString()), "tasks_limit");
        content.Add(new StringContent(MaxRetries  .ToString()), "max_retries");

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
            FilePath       = filePath,
            FileSize       = fileInfo.Length,
            Status         = isSuccess ? "Success" : "Failed",
            AttemptNumber  = 1,
            TaskId         = taskId,
            HttpStatusCode = statusCode,
            ErrorMessage   = isSuccess ? null : (apiMessage ?? body),
            Timestamp      = DateTime.UtcNow,
        };
    }

    private void ReportProgress()
    {
        int completed = _allResults.Count;
        int total     = Math.Max(_totalFiles, 1);

        var record = new ProgressRecord(0, "AstrBot 知识库上传",
            $"已完成 {completed} / {total} 个文件")
        {
            PercentComplete  = Math.Min(completed * 100 / total, 100),
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
