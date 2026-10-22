namespace MarkdownRagMcp.Domain;

/// <summary>
/// A single chunk of a markdown document together with the metadata needed to
/// trace it back to its source file. Mirrors the <c>Entity</c> model in the
/// original Python implementation.
/// </summary>
public sealed class ChunkEntity
{
    public required string Text { get; init; }
    public required string Filename { get; init; }
    public required string Path { get; init; }
}

/// <summary>
/// One search hit: a distance (lower = more similar for cosine) and the source chunk.
/// Mirrors the <c>SearchResult</c> model in the original implementation.
/// </summary>
public sealed record SearchResult(double Distance, ChunkEntity Entity);

/// <summary>Uniform error shape returned by the HTTP surface (the MCP surface returns protocol-level errors instead).</summary>
public sealed record ErrorResponse(string Error, string Message);
