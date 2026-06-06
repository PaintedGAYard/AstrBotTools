namespace AstrBotTools;

/// <summary>
/// AstrBot 知识库文档上传的 API 参数。
/// 这些参数通过 multipart/form-data 随文件一起发送到 POST /api/kb/document/upload。
/// </summary>
public sealed class UploadParameters
{
    /// <summary>目标知识库 ID</summary>
    public string KbId { get; init; } = string.Empty;

    /// <summary>切块大小（字符数）</summary>
    public int ChunkSize { get; init; } = 512;

    /// <summary>切块重叠（字符数）</summary>
    public int ChunkOverlap { get; init; } = 50;

    /// <summary>向量化批处理大小</summary>
    public int BatchSize { get; init; } = 32;

    /// <summary>AstrBot 后台向量化的最大重试次数</summary>
    public int MaxRetries { get; init; } = 3;
}
