using MarkdownRagMcp.Abstractions;
using MarkdownRagMcp.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.Tokenizers;

namespace MarkdownRagMcp.Embeddings;

/// <summary>
/// Lazily-constructed BERT tokenizer shared by the chunker and the embedding provider, so
/// both use the same vocabulary from the downloaded model directory. The heavy vocab file is
/// loaded only on first use, which keeps startup fast and avoids loading it twice.
/// </summary>
public sealed class LazyBertTokenizer : ITextTokenizer, IDisposable
{
    private readonly MarkdownRagOptions _options;
    private readonly HuggingFaceModelDownloader _downloader;
    private readonly ILogger<LazyBertTokenizer> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private BertTokenizer? _tokenizer;
    private string? _modelDir;
    private bool _initialized;

    public LazyBertTokenizer(
        IOptions<MarkdownRagOptions> options,
        HuggingFaceModelDownloader downloader,
        ILogger<LazyBertTokenizer> logger)
    {
        _options = options.Value;
        _downloader = downloader;
        _logger = logger;
    }

    /// <summary>Resolve the underlying BertTokenizer, downloading the model on first access.</summary>
    public BertTokenizer GetTokenizer()
    {
        if (_initialized) return _tokenizer!;
        _gate.Wait();
        try
        {
            if (_initialized) return _tokenizer!;
            // The download runs on a background thread so callers (e.g. the embedding
            // provider during preload) don't block a request thread for tens of seconds.
            _modelDir = Task.Run(() =>
                _downloader.EnsureModelAsync(_options.Embedding.ModelId, _options.Embedding.ModelCachePath, CancellationToken.None))
                .GetAwaiter().GetResult();
            var vocabPath = Path.Combine(_modelDir, "vocab.txt");
            _logger.LogInformation("Loading BERT vocabulary from {Path}", vocabPath);
            _tokenizer = BertTokenizer.Create(vocabPath, new BertOptions());
            _initialized = true;
            return _tokenizer;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Absolute path to the directory holding model.onnx and vocab.txt.</summary>
    public string GetModelDirectory()
    {
        if (_initialized) return _modelDir!;
        return GetTokenizer() is not null ? _modelDir! : throw new InvalidOperationException("Tokenizer not initialized.");
    }

    public int CountTokens(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }
        return GetTokenizer().EncodeToIds(text, addSpecialTokens: false).Count;
    }

    public void Dispose() => _gate.Dispose();
}
