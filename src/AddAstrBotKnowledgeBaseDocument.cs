using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Net.Http.Headers;

namespace AstrBotTools;

/// <summary>
/// Uploads one or more files to an AstrBot knowledge base and tracks server-side vectorization.
/// Acts as a task factory: creates <see cref="DocumentUploadWorker"/> instances per file and
/// controls concurrency via <see cref="ConcurrencyLimit"/>.
/// </summary>
[Cmdlet(VerbsCommon.Add, "AstrBotKnowledgeBaseDocument",
        SupportsShouldProcess = true,
        DefaultParameterSetName = "Path")]
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

    #region Internal state

    private HttpClient _httpClient = null!;
    private IConsoleWriter _console = null!;
    private readonly List<(Task<UploadResult> Task, DocumentUploadWorker Worker)> _runningWork = new();
    private readonly List<UploadResult> _allResults = new();
    private int _totalFiles;
    private CancellationTokenSource _cts = null!;

    #endregion

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

                // Drain output after every worker creation for real-time feedback
                DrainAllOutput();

                // Backpressure: block when concurrency limit is reached
                if (_runningWork.Count >= ConcurrencyLimit)
                {
                    DrainCompletedWorkers();
                    DrainAllOutput();
                }
            }
        }

        // Final drain before returning from ProcessRecord
        DrainAllOutput();
    }

    protected override void EndProcessing()
    {
        // Drain any output produced after the last ProcessRecord call
        DrainAllOutput();

        // Poll waiting workers every 500ms, draining output continuously
        while (_runningWork.Count > 0)
        {
            var tasks = _runningWork.Select(x => (Task)x.Task).ToArray();
            try
            {
                Task.WaitAny(tasks, 500, _cts.Token);
            }
            catch (OperationCanceledException) { break; }

            DrainAllOutput();
            CollectCompletedResults();
        }

        // Final drain after all workers are done
        DrainAllOutput();
        CollectCompletedResults();

        // Emit per-file results
        foreach (var result in _allResults)
            WriteObject(result);

        // Summary report
        int successCount = _allResults.Count(r => r.Status is "Success" or "RetrySuccess");
        int failCount    = _allResults.Count(r => r.Status == "Failed");
        var failedFiles  = _allResults
            .Where(r => r.Status == "Failed")
            .Select(r => r.FileName);

        WriteVerbose(
            $"════ Upload complete ════ " +
            $"Total: {_totalFiles}, Succeeded: {successCount}, Failed: {failCount}");

        if (failCount > 0)
        {
            WriteVerbose($"Failed files: {string.Join(", ", failedFiles)}");
        }
    }

    protected override void StopProcessing()
    {
        _cts?.Cancel();
    }

    #region Concurrency control

    /// <summary>
    /// Blocks until at least one worker completes, draining output every 500 ms.
    /// Returns when the concurrency slot count drops below <see cref="ConcurrencyLimit"/>.
    /// </summary>
    private void DrainCompletedWorkers()
    {
        while (_runningWork.Count >= ConcurrencyLimit)
        {
            var tasks = _runningWork.Select(x => (Task)x.Task).ToArray();
            int doneIndex = Task.WaitAny(tasks, 500, _cts.Token);

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
    /// Drains pending output from all active workers.
    /// </summary>
    private void DrainAllOutput()
    {
        foreach (var (_, worker) in _runningWork)
            worker.DrainOutput(_console);
    }

    /// <summary>
    /// Collects results from all currently completed workers.
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
            // Worker catches all exceptions internally; this branch should be unreachable.
            var ex = task.Exception?.InnerException ?? task.Exception;
            _allResults.Add(new UploadResult
            {
                FileName     = "Unknown",
                Status       = "Failed",
                ErrorMessage = $"Internal error: {ex?.Message}",
                Timestamp    = DateTime.UtcNow,
            });
        }
        // Cancelled tasks are silently ignored.
    }

    #endregion

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _httpClient?.Dispose();
        GC.SuppressFinalize(this);
    }
}
