namespace AstrBotTools;

/// <summary>
/// API parameters for AstrBot knowledge base document upload.
/// These are sent as multipart/form-data fields alongside the file to <c>POST /api/kb/document/upload</c>.
/// </summary>
public sealed class UploadParameters
{
    /// <summary>Target knowledge base ID.</summary>
    public string KbId { get; init; } = string.Empty;

    /// <summary>Chunk size in characters (sent as <c>chunk_size</c>).</summary>
    public int ChunkSize { get; init; } = 512;

    /// <summary>Chunk overlap in characters (sent as <c>chunk_overlap</c>).</summary>
    public int ChunkOverlap { get; init; } = 50;

    /// <summary>Batch size for server-side embedding vectorization (sent as <c>batch_size</c>).</summary>
    public int BatchSize { get; init; } = 32;

    /// <summary>Maximum retries for server-side vectorization on failure (sent as <c>max_retries</c>).</summary>
    public int MaxRetries { get; init; } = 3;
}
