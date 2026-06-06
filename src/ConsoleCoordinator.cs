using System.Management.Automation;

namespace AstrBotTools;

/// <summary>
/// 控制台输出协调器。
/// 通过 lock 序列化所有 Write* 调用，确保多 Worker 并发时输出不交错。
/// </summary>
internal sealed class ConsoleCoordinator : IConsoleWriter
{
    private readonly PSCmdlet _cmdlet;
    private readonly object _lock = new();

    public ConsoleCoordinator(PSCmdlet cmdlet)
    {
        _cmdlet = cmdlet;
    }

    public void WriteVerbose(string message)
    {
        lock (_lock)
        {
            _cmdlet.WriteVerbose(message);
        }
    }

    public void WriteWarning(string message)
    {
        lock (_lock)
        {
            _cmdlet.WriteWarning(message);
        }
    }

    public void WriteProgress(ProgressRecord record)
    {
        lock (_lock)
        {
            _cmdlet.WriteProgress(record);
        }
    }
}
