using System.Management.Automation;

namespace AstrBotTools;

/// <summary>
/// 控制台输出抽象接口。
/// Worker 通过此接口输出信息，由 ConsoleCoordinator 统一序列化，
/// 避免多 Worker 并发写入导致输出交错。
/// </summary>
internal interface IConsoleWriter
{
    void WriteVerbose(string message);
    void WriteWarning(string message);
    void WriteProgress(ProgressRecord record);
}
