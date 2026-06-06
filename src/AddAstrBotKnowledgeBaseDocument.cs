using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Net.Http.Headers;

namespace AstrBotTools;

[Cmdlet(VerbsCommon.Add, "AstrBotKnowledgeBaseDocument",
        SupportsShouldProcess = true,
        DefaultParameterSetName = "Path")]
[Alias("Add-KBDoc")]
[OutputType(typeof(UploadResult))]
public class AddAstrBotKnowledgeBaseDocument : PSCmdlet, IDisposable
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
    [Parameter()] public int MaxRetries   { get; set; } = 3;

    [Parameter()]
    [ValidateRange(1, 100)]
    public int ConcurrencyLimit { get; set; } = 3;

    [Parameter()]
    [ValidateRange(0, 20)]
    public int UploadRetryLimit { get; set; } = 3;

    // ========== 内部状态 ==========

    private HttpClient _httpClient = null!;
    private IConsoleWriter _console = null!;
    private readonly List<(Task<UploadResult> Task, DocumentUploadWorker Worker)> _runningWork = new();
    private readonly List<UploadResult> _allResults = new();
    private int _totalFiles;
    private CancellationTokenSource _cts = null!;

    protected override void BeginProcessing()
    {
        _cts = new CancellationTokenSource();
        _console = new ConsoleCoordinator(this);

        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = ConcurrencyLimit,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        };

        _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(180) };
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", AuthToken);
    }

    protected override void ProcessRecord()
    {
        var uploadParams = new UploadParameters
        {
            KbId         = KbId,
            ChunkSize    = ChunkSize,
            ChunkOverlap = ChunkOverlap,
            BatchSize    = BatchSize,
            MaxRetries   = MaxRetries,
        };

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
                if (!System.IO.File.Exists(providerPath))
                {
                    WriteWarning($"不是文件，跳过: {providerPath}");
                    continue;
                }

                _totalFiles++;

                var worker = new DocumentUploadWorker(
                    _httpClient, BaseUrl, providerPath, uploadParams,
                    UploadRetryLimit);

                var task = worker.ExecuteAsync(_cts.Token);
                _runningWork.Add((task, worker));

                // 背压：达到并发上限时，等待至少一个 Worker 完成
                if (_runningWork.Count >= ConcurrencyLimit)
                    DrainCompletedWorkers();
            }
        }
    }

    protected override void EndProcessing()
    {
        // 等待所有剩余 Worker 完成
        try
        {
            Task.WaitAll(_runningWork.Select(x => x.Task).ToArray(), _cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (AggregateException) { }

        // 在 pipeline 线程上 drain 所有 Worker 的待输出消息
        foreach (var (_, worker) in _runningWork)
            worker.DrainOutput(_console);

        CollectCompletedResults();

        // 输出结果
        foreach (var result in _allResults)
            WriteObject(result);

        // 汇总报告
        int successCount = _allResults.Count(r => r.Status is "Success" or "RetrySuccess");
        int failCount    = _allResults.Count(r => r.Status == "Failed");
        var failedFiles  = _allResults
            .Where(r => r.Status == "Failed")
            .Select(r => r.FileName);

        WriteVerbose(
            $"════ 上传完成 ════ " +
            $"总计 {_totalFiles} 个文件，成功 {successCount} 个，失败 {failCount} 个");

        if (failCount > 0)
        {
            WriteVerbose($"失败文件列表: {string.Join(", ", failedFiles)}");
        }
    }

    protected override void StopProcessing()
    {
        _cts?.Cancel();
    }

    // ========== 并发控制 ==========

    /// <summary>
    /// 等待至少一个 Worker 完成，收集其结果，释放并发槽位。
    /// </summary>
    private void DrainCompletedWorkers()
    {
        while (_runningWork.Count >= ConcurrencyLimit)
        {
            int doneIndex = Task.WaitAny(
                _runningWork.Select(x => x.Task).ToArray(), _cts.Token);
            if (doneIndex < 0) break;

            // 收集所有已完成的 Task
            var completed = _runningWork.Where(x => x.Task.IsCompleted).ToArray();
            foreach (var (task, worker) in completed)
            {
                _runningWork.Remove((task, worker));
                worker.DrainOutput(_console);
                CollectResult(task);
            }
        }
    }

    /// <summary>
    /// 收集当前所有已完成 Worker 的结果。
    /// </summary>
    private void CollectCompletedResults()
    {
        var completed = _runningWork.Where(x => x.Task.IsCompleted).ToArray();
        foreach (var (task, worker) in completed)
        {
            _runningWork.Remove((task, worker));
            worker.DrainOutput(_console);
            CollectResult(task);
        }
    }

    private void CollectResult(Task<UploadResult> task)
    {
        if (task.IsCompletedSuccessfully)
        {
            _allResults.Add(task.Result);
        }
        else if (task.IsFaulted)
        {
            // 正常情况下 Worker 内部已处理所有异常，不应走到这里
            var ex = task.Exception?.InnerException ?? task.Exception;
            _allResults.Add(new UploadResult
            {
                FileName     = "未知",
                Status       = "Failed",
                ErrorMessage = $"内部错误: {ex?.Message}",
                Timestamp    = DateTime.UtcNow,
            });
        }
        // cancelled: 忽略
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _httpClient?.Dispose();
        GC.SuppressFinalize(this);
    }
}
