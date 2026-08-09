using MarkdownRagMcp.Domain;

namespace MarkdownRagMcp.Abstractions;

/// <summary>
/// Splits markdown documents into structurally coherent chunks.
/// The default implementation mirrors the original's two-stage pipeline
/// (heading-based <c>MarkdownNodeParser</c> followed by a <c>TokenTextSplitter</c>).
/// </summary>
public interface IMarkdownChunker
{
    /// <summary>
    /// Chunk the given markdown text into one or more <see cref="ChunkEntity"/> instances,
    /// preserving the source file name and absolute path metadata.
    /// </summary>
    IReadOnlyList<ChunkEntity> Chunk(string markdownText, string fileName, string filePath);
}
