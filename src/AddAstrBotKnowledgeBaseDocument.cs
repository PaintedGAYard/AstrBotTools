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

                // 每次添加 Worker 后都排空一次输出，确保实时反馈
                DrainAllOutput();

                // 背压：达到并发上限时，等待至少一个 Worker 完成
                if (_runningWork.Count >= ConcurrencyLimit)
                {
                    DrainCompletedWorkers();
                    // DrainCompletedWorkers 退出后可能有残余输出
                    DrainAllOutput();
                }
            }
        }

        // 每次 ProcessRecord 结束前排空
        DrainAllOutput();
    }

    protected override void EndProcessing()
    {
        // 进入 EndProcessing 前排空最后一次 ProcessRecord 后产生的输出
        DrainAllOutput();

        // 等待剩余 Worker + 定期 drain 输出，实现实时进度
        while (_runningWork.Count > 0)
        {
            var tasks = _runningWork.Select(x => (Task)x.Task).ToArray();
            try
            {
                Task.WaitAny(tasks, 500, _cts.Token);
            }
            catch (OperationCanceledException) { break; }

            // 定期排空所有 Worker 的待输出消息
            DrainAllOutput();

            // 收集已完成的
            CollectCompletedResults();
        }

        // 最后一次排空
        DrainAllOutput();
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
    /// 等待至少一个 Worker 完成，每 500ms 排空一次输出，
    /// 实现实时进度显示。释放并发槽位后返回。
    /// </summary>
    private void DrainCompletedWorkers()
    {
        while (_runningWork.Count >= ConcurrencyLimit)
        {
            var tasks = _runningWork.Select(x => (Task)x.Task).ToArray();
            int doneIndex = Task.WaitAny(tasks, 500, _cts.Token);

            // 定期排空所有 Worker 的待输出消息
            DrainAllOutput();

            if (doneIndex >= 0)
            {
                var completed = _runningWork.Where(x => x.Task.IsCompleted).ToArray();
                foreach (var (task, worker) in completed)
                {
                    _runningWork.Remove((task, worker));
                    CollectResult(task);
                }
            }
        }
    }

    /// <summary>
    /// 排空当前所有 Worker 的待输出消息。
    /// </summary>
    private void DrainAllOutput()
    {
        foreach (var (_, worker) in _runningWork)
            worker.DrainOutput(_console);
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
