namespace AstrBotTools;

/// <summary>
/// Per-file upload and vectorization result.
/// This is the primary output type of the <c>Add-AstrBotKnowledgeBaseDocument</c> cmdlet.
/// All properties are visible via Format-List; a subset is shown in the default table view
/// controlled by <c>AstrBotTools.format.ps1xml</c>.
/// </summary>
public sealed class UploadResult
{
    /// <summary>File name without directory path.</summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>Full file path.</summary>
    public string FilePath { get; init; } = string.Empty;

    /// <summary>File size in bytes.</summary>
    public long FileSize { get; init; }

    /// <summary>
    /// Overall status: Success, RetrySuccess, Failed, VectorizeFailed, or VectorizeTimeout.
    /// <c>Success</c>/<c>RetrySuccess</c> indicate both HTTP upload and server-side vectorization succeeded.
    /// </summary>
    public string Status { get; init; } = "Unknown";

    /// <summary>Number of upload attempts made (meaningful for Success/RetrySuccess).</summary>
    public int AttemptNumber { get; init; }

    /// <summary>AstrBot server-side vectorization task ID.</summary>
    public string? TaskId { get; init; }

    /// <summary>HTTP status code from the upload response.</summary>
    public int? HttpStatusCode { get; init; }

    /// <summary>Error message when the overall result is a failure.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Server-side vectorization sub-status: Completed, Failed, Timeout, or null when the upload itself failed.
    /// </summary>
    public string? VectorizeStatus { get; init; }

    /// <summary>Knowledge base document ID (set when vectorization succeeds).</summary>
    public string? DocId { get; init; }

    /// <summary>Number of chunks produced by vectorization.</summary>
    public int ChunkCount { get; init; }

    /// <summary>UTC timestamp when the result was produced.</summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}
