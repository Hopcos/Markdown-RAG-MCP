using System.IO.Compression;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;

namespace MarkdownRagMcp.Embeddings;

/// <summary>
/// Downloads and caches a Hugging Face embedding model as ONNX + vocab artifacts.
/// Reproduces the original "first run downloads the ~model" experience without requiring
/// Python: files are fetched from the HF Hub and unpacked under <c>ModelCachePath</c>.
/// </summary>
public sealed class HuggingFaceModelDownloader
{
    private readonly HttpClient _http;
    private readonly ILogger<HuggingFaceModelDownloader> _logger;

    public HuggingFaceModelDownloader(HttpClient http, ILogger<HuggingFaceModelDownloader> logger)
    {
        _http = http;
        _logger = logger;
        // HF can return large files; keep generous timeouts for first-run downloads.
        _http.Timeout = TimeSpan.FromMinutes(10);
    }

    /// <summary>
    /// Ensure the requested model is present locally. Returns the absolute path to the
    /// directory containing <c>model.onnx</c> and <c>vocab.txt</c>.
    /// </summary>
    public async Task<string> EnsureModelAsync(string modelId, string cacheRoot, CancellationToken ct = default)
    {
        var modelDir = Path.GetFullPath(Path.Combine(cacheRoot, modelId.Replace('/', Path.DirectorySeparatorChar)));
        var onnxPath = Path.Combine(modelDir, "model.onnx");
        var vocabPath = Path.Combine(modelDir, "vocab.txt");

        if (File.Exists(onnxPath) && File.Exists(vocabPath))
        {
            return modelDir;
        }

        Directory.CreateDirectory(modelDir);
        _logger.LogInformation("Downloading embedding model '{ModelId}' (first run only)...", modelId);

        // Prefer a repo that ships an ONNX export plus a BERT-style vocab. Fall back across
        // a short list of compatible, permissively-licensed sentence-transformer ONNX repos.
        foreach (var candidate in CandidateRepos(modelId))
        {
            try
            {
                await DownloadFileAsync(candidate.OnnxUrl, onnxPath, ct);
                await DownloadFileAsync(candidate.VocabUrl, vocabPath, ct);
                _logger.LogInformation("Embedding model cached at {Path}", modelDir);
                return modelDir;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("Candidate '{Repo}' failed: {Message}. Trying next.", candidate.OnnxUrl, ex.Message);
            }
        }

        throw new InvalidOperationException(
            $"Could not download embedding model '{modelId}'. Pre-download model.onnx and vocab.txt into '{modelDir}'.");
    }

    private static IEnumerable<Candidate> CandidateRepos(string modelId)
    {
        // 1. The exact model id with onnx subfolder.
        yield return new Candidate(
            OnnxUrl: $"https://huggingface.co/{modelId}/resolve/main/onnx/model.onnx",
            VocabUrl: $"https://huggingface.co/{modelId}/resolve/main/vocab.txt");

        // 2. A widely-used MiniLM ONNX export that pairs with a BERT vocab.txt.
        yield return new Candidate(
            OnnxUrl: "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/onnx/model.onnx",
            VocabUrl: "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/vocab.txt");

        // 3. q3dp/all-MiniLM-L6-v2-onnx (dedicated ONNX repo, 384-dim).
        yield return new Candidate(
            OnnxUrl: "https://huggingface.co/q3dp/all-MiniLM-L6-v2-onnx/resolve/main/model.onnx",
            VocabUrl: "https://huggingface.co/google-bert/bert-base-uncased/resolve/main/vocab.txt");
    }

    private async Task DownloadFileAsync(string url, string destinationPath, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        await using var content = await resp.Content.ReadAsStreamAsync(ct);
        await using var file = File.Create(destinationPath);
        await content.CopyToAsync(file, 81920, ct);
    }

    private sealed record Candidate(string OnnxUrl, string VocabUrl);
}
