using MarkdownRagMcp.Abstractions;
using MarkdownRagMcp.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace MarkdownRagMcp.Embeddings;

/// <summary>
/// Local-first embedding provider. Runs a Hugging Face sentence-transformer model exported
/// to ONNX, performs mean-pooling over the token dimension (with attention masking) and
/// L2-normalizes the result — the same post-processing pymilvus's
/// <c>DefaultEmbeddingFunction</c> applies. Equivalent to the original's
/// <c>encode_queries</c>/<c>encode_documents</c> paths (both reduce to a forward pass here).
/// </summary>
/// <remarks>
/// The model + tokenizer are loaded on demand. Call <see cref="EnsureLoadedAsync"/> once at
/// startup (via <c>EmbeddingPreloadHostedService</c>) so the first tool call does not pay the
/// load cost inside the MCP request path and time out. Embeddings are batched into a single ONNX
/// <c>Run</c> per chunk batch to bound first-call latency.
/// </remarks>
public sealed class OnnxEmbeddingProvider : IEmbeddingProvider, IDisposable
{
    private readonly EmbeddingOptions _options;
    private readonly ILogger<OnnxEmbeddingProvider> _logger;
    private readonly LazyBertTokenizer _tokenizerSource;

    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private BertTokenizer? _tokenizer;
    private InferenceSession? _session;
    private string? _modelDir;
    private volatile bool _loaded;

    public OnnxEmbeddingProvider(
        IOptions<MarkdownRagOptions> options,
        LazyBertTokenizer tokenizerSource,
        ILogger<OnnxEmbeddingProvider> logger)
    {
        _options = options.Value.Embedding;
        _logger = logger;
        _tokenizerSource = tokenizerSource;
    }

    public int Dimension => _options.Dimension;

    public Task<IReadOnlyList<float[]>> EmbedQueriesAsync(IReadOnlyList<string> queries, CancellationToken ct = default)
        => EmbedAsync(queries, ct);

    public Task<IReadOnlyList<float[]>> EmbedDocumentsAsync(IReadOnlyList<string> documents, CancellationToken ct = default)
        => EmbedAsync(documents, ct);

    /// <summary>
    /// Load the model and tokenizer if not already loaded. Safe to call concurrently; the
    /// second caller awaits the first. Returns quickly when already loaded.
    /// </summary>
    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (_loaded) return;
        await _loadGate.WaitAsync(ct);
        try
        {
            if (_loaded) return;
            // Run the CPU-bound model/tokenizer load off the calling thread.
            await Task.Run(LoadCore, ct);
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private void LoadCore()
    {
        if (_loaded) return;
        _tokenizer = _tokenizerSource.GetTokenizer();
        _modelDir = _tokenizerSource.GetModelDirectory();
        var onnxPath = Path.Combine(_modelDir, "model.onnx");

        _logger.LogInformation("Loading ONNX embedding model from {Path}", onnxPath);
        var sessionOptions = new SessionOptions();
        // CPU keeps the server deterministic and dependency-free; swap to a provider extension
        // (CUDA/DirectML) for GPU acceleration without touching call sites.
        sessionOptions.AppendExecutionProvider_CPU();
        _session = new InferenceSession(onnxPath, sessionOptions);
        _loaded = true;
        _logger.LogInformation("Embedding model loaded (dim={Dim})", _options.Dimension);
    }

    private async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        if (texts.Count == 0)
        {
            return Array.Empty<float[]>();
        }

        await EnsureLoadedAsync(ct);

        // Tokenize + pad the batch, then run one forward pass for the whole batch off the
        // async thread. One Run call for N texts is far cheaper than N separate runs and keeps
        // first-call latency well under the typical MCP client timeout.
        var seqLen = _options.MaxSequenceLength;
        var padId = _tokenizer!.PaddingTokenId;
        var batchSize = texts.Count;

        var inputIds = new long[batchSize * seqLen];
        var attentionMask = new long[batchSize * seqLen];
        var tokenTypeIds = new long[batchSize * seqLen];

        for (var b = 0; b < batchSize; b++)
        {
            ct.ThrowIfCancellationRequested();
            var tokenIds = _tokenizer.EncodeToIds(texts[b], addSpecialTokens: true);
            var take = Math.Min(tokenIds.Count, seqLen);
            var off = b * seqLen;
            for (var i = 0; i < take; i++)
            {
                inputIds[off + i] = tokenIds[i];
                attentionMask[off + i] = 1;
            }
            for (var i = take; i < seqLen; i++)
            {
                inputIds[off + i] = padId;
            }
        }

        return await Task.Run(() => RunInference(
            inputIds, attentionMask, tokenTypeIds, batchSize, seqLen), ct);
    }

    private float[][] RunInference(
        long[] inputIds, long[] attentionMask, long[] tokenTypeIds, int batchSize, int seqLen)
    {
        var inputIdsTensor = new DenseTensor<long>(inputIds, new[] { batchSize, seqLen });
        var attentionTensor = new DenseTensor<long>(attentionMask, new[] { batchSize, seqLen });
        var tokenTypeTensor = new DenseTensor<long>(tokenTypeIds, new[] { batchSize, seqLen });

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIdsTensor),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionTensor),
            NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeTensor),
        };

        using var results = _session!.Run(inputs);
        var tensor = results.First().AsTensor<float>();
        var dims = tensor.Dimensions;

        var embeddings = new float[batchSize][];

        if (dims.Length == 2)
        {
            // [batch, dim] — already pooled+normalized by the export.
            var dim = (int)dims[1];
            var flat = tensor.ToArray();
            for (var b = 0; b < batchSize; b++)
            {
                var slice = new float[dim];
                Array.Copy(flat, b * dim, slice, 0, dim);
                embeddings[b] = slice;
            }
        }
        else
        {
            // [batch, seq, dim] — mean-pool over the seq dimension with attention masking.
            var dim = (int)dims[2];
            for (var b = 0; b < batchSize; b++)
            {
                embeddings[b] = L2Normalize(
                    PoolTokenEmbeddings(tensor, attentionMask, b, seqLen, dim));
            }
        }

        return embeddings;
    }

    private static float[] PoolTokenEmbeddings(
        Tensor<float> tokenEmbeddings, long[] attentionMask, int batch, int seqLen, int dim)
    {
        var pooled = new float[dim];
        var sumMask = 0;
        var off = batch * seqLen;

        for (var t = 0; t < seqLen; t++)
        {
            if (attentionMask[off + t] == 0)
            {
                continue;
            }
            sumMask++;
            for (var d = 0; d < dim; d++)
            {
                pooled[d] += tokenEmbeddings[batch, t, d];
            }
        }

        if (sumMask > 0)
        {
            for (var d = 0; d < dim; d++)
            {
                pooled[d] /= sumMask;
            }
        }

        return pooled;
    }

    private static float[] L2Normalize(float[] v)
    {
        var norm = (float)Math.Sqrt(v.Sum(x => x * x));
        if (norm < 1e-12f)
        {
            return v;
        }
        var result = new float[v.Length];
        for (var i = 0; i < v.Length; i++)
        {
            result[i] = v[i] / norm;
        }
        return result;
    }

    public void Dispose()
    {
        _session?.Dispose();
        _loadGate.Dispose();
    }
}
