namespace AstrBotTools;

/// <summary>
/// 每个文件的上传结果。
/// 作为 Cmdlet 的主输出，自动显示所有属性。
/// </summary>
public sealed class UploadResult
{
    /// <summary>文件名（不含路径）</summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>完整路径</summary>
    public string FilePath { get; init; } = string.Empty;

    /// <summary>文件大小（字节）</summary>
    public long FileSize { get; init; }

    /// <summary>上传状态：Success / RetrySuccess / Failed</summary>
    public string Status { get; init; } = "Unknown";

    /// <summary>最终成功时的尝试次数（仅 Success/RetrySuccess 有意义）</summary>
    public int AttemptNumber { get; init; }

    /// <summary>AstrBot 后台向量化任务 ID</summary>
    public string? TaskId { get; init; }

    /// <summary>HTTP 状态码</summary>
    public int? HttpStatusCode { get; init; }

    /// <summary>错误信息（失败时）</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>向量化处理状态：Completed / Failed / Timeout / null（上传失败时）</summary>
    public string? VectorizeStatus { get; init; }

    /// <summary>知识库中文档 ID（向量化成功后）</summary>
    public string? DocId { get; init; }

    /// <summary>向量化后的切块数量</summary>
    public int ChunkCount { get; init; }

    /// <summary>完成时间</summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}
