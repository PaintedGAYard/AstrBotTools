using System.Management.Automation;

namespace AstrBotTools;

/// <summary>
/// Abstraction for console output from worker threads.
/// Implementations serialize calls via <c>lock</c> so that multiple workers
/// running concurrently do not interleave their output on the pipeline thread.
/// </summary>
internal interface IConsoleWriter
{
    void WriteVerbose(string message);
    void WriteWarning(string message);
    void WriteProgress(ProgressRecord record);
}
