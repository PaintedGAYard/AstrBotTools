using System.Management.Automation;

namespace AstrBotTools;

/// <summary>
/// Serializes <c>Write*</c> calls from multiple workers via <c>lock</c>,
/// ensuring console output is not interleaved on the single pipeline thread.
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
        lock (_lock) { _cmdlet.WriteVerbose(message); }
    }

    public void WriteWarning(string message)
    {
        lock (_lock) { _cmdlet.WriteWarning(message); }
    }

    public void WriteProgress(ProgressRecord record)
    {
        lock (_lock) { _cmdlet.WriteProgress(record); }
    }
}
