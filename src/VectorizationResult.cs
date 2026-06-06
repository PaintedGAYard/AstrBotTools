namespace AstrBotTools;

/// <summary>
/// AstrBot 后台向量化处理的结果。
/// 由 <see cref="VectorizationProgressTracker"/> 轮询 <c>GET /api/kb/document/upload/progress</c> 获得。
/// </summary>
internal sealed class VectorizationResult
{
    /// <summary>向量化状态：Completed / Failed / Timeout</summary>
    public string Status { get; init; } = "Unknown";

    /// <summary>知识库中文档 ID（仅 Completed 时有值）</summary>
    public string? DocId { get; init; }

    /// <summary>切块数量（仅 Completed 时有值）</summary>
    public int ChunkCount { get; init; }

    /// <summary>错误信息（Failed / Timeout 时）</summary>
    public string? ErrorMessage { get; init; }
}
