using MarkdownRagMcp.Abstractions;
using MarkdownRagMcp.Domain;

namespace McpMarkdownRag.Tests.Support;

/// <summary>
/// Deterministic in-memory <see cref="IEmbeddingProvider"/> for unit and integration tests.
/// Generates synthetic, predictable unit-normalized vectors without downloading models or running ONNX.
/// </summary>
public sealed class FakeEmbeddingProvider : IEmbeddingProvider
{
    public int Dimension { get; }
    public int CallCount { get; private set; }

    public FakeEmbeddingProvider(int dimension = 4)
    {
        Dimension = dimension;
    }

    public Task<IReadOnlyList<float[]>> EmbedQueriesAsync(IReadOnlyList<string> queries, CancellationToken ct = default)
        => EmbedTextsAsync(queries);

    public Task<IReadOnlyList<float[]>> EmbedDocumentsAsync(IReadOnlyList<string> documents, CancellationToken ct = default)
        => EmbedTextsAsync(documents);

    private Task<IReadOnlyList<float[]>> EmbedTextsAsync(IReadOnlyList<string> texts)
    {
        CallCount++;
        var list = new List<float[]>(texts.Count);
        foreach (var text in texts)
        {
            list.Add(CreateVector(text, Dimension));
        }
        return Task.FromResult<IReadOnlyList<float[]>>(list);
    }

    /// <summary>
    /// Generates a deterministic normalized float vector based on simple character hash.
    /// </summary>
    public static float[] CreateVector(string text, int dimension)
    {
        var vec = new float[dimension];
        var hash = (uint)text.GetHashCode();
        for (var i = 0; i < dimension; i++)
        {
            vec[i] = ((hash >> (i % 16)) & 0xFF) / 255.0f;
        }

        // L2 Normalize
        var sumSq = 0.0f;
        for (var i = 0; i < dimension; i++)
        {
            sumSq += vec[i] * vec[i];
        }

        if (sumSq > 0f)
        {
            var norm = MathF.Sqrt(sumSq);
            for (var i = 0; i < dimension; i++)
            {
                vec[i] /= norm;
            }
        }
        else
        {
            vec[0] = 1.0f;
        }

        return vec;
    }
}

/// <summary>
/// Simple white-space / word-level tokenizer for tests avoiding BERT vocab loading.
/// </summary>
public sealed class SimpleTestTokenizer : ITextTokenizer
{
    public int CountTokens(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        return text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    }
}
