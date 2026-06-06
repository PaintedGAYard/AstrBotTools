namespace AstrBotTools;

/// <summary>
/// Represents the result of AstrBot server-side vectorization.
/// Obtained by <see cref="VectorizationProgressTracker"/> polling
/// <c>GET /api/kb/document/upload/progress</c>.
/// </summary>
internal sealed class VectorizationResult
{
    /// <summary>Vectorization status: Completed, Failed, or Timeout.</summary>
    public string Status { get; init; } = "Unknown";

    /// <summary>Document ID in the knowledge base (only when Status is Completed).</summary>
    public string? DocId { get; init; }

    /// <summary>Number of chunks after vectorization (only when Status is Completed).</summary>
    public int ChunkCount { get; init; }

    /// <summary>Error message when Status is Failed or Timeout.</summary>
    public string? ErrorMessage { get; init; }
}
