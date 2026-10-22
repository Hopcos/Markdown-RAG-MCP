using MarkdownRagMcp.Domain;

namespace MarkdownRagMcp.Abstractions;

/// <summary>
/// Converts text into fixed-dimension floating-point vectors.
/// The default implementation runs a local ONNX BERT model; the abstraction lets
/// alternative providers (cloud embeddings, ONNX GenAI, etc.) be swapped in via DI
/// without touching call sites.
/// </summary>
public interface IEmbeddingProvider
{
    /// <summary>Dimensionality of every vector this provider emits.</summary>
    int Dimension { get; }

    /// <summary>Embed a set of queries (the <c>encode_queries</c> path).</summary>
    Task<IReadOnlyList<float[]>> EmbedQueriesAsync(IReadOnlyList<string> queries, CancellationToken ct = default);

    /// <summary>Embed a set of documents/chunks (the <c>encode_documents</c> path).</summary>
    Task<IReadOnlyList<float[]>> EmbedDocumentsAsync(IReadOnlyList<string> documents, CancellationToken ct = default);
}
