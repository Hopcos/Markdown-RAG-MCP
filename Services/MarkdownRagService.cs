using System.IO.Abstractions;
using MarkdownRagMcp.Abstractions;
using MarkdownRagMcp.Configuration;
using MarkdownRagMcp.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarkdownRagMcp.Services;

/// <summary>
/// Orchestrates the two phases of the RAG server — indexing and searching — coordinating
/// the chunker, embedding provider, vector store, file discovery and tracker. Keeps the
/// MCP tool classes thin: each tool delegates here so the protocol layer stays decoupled
/// from the domain logic and is easy to unit-test.
/// </summary>
public sealed class MarkdownRagService
{
    private readonly IMarkdownChunker _chunker;
    private readonly IEmbeddingProvider _embeddings;
    private readonly IVectorStore _vectors;
    private readonly IMarkdownFileDiscovery _discovery;
    private readonly IIndexTracker _tracker;
    private readonly IFileSystem _fs;
    private readonly MarkdownRagOptions _options;
    private readonly ILogger<MarkdownRagService> _logger;
    private readonly SemaphoreSlim _indexLock = new(1, 1);

    public MarkdownRagService(
        IMarkdownChunker chunker,
        IEmbeddingProvider embeddings,
        IVectorStore vectors,
        IMarkdownFileDiscovery discovery,
        IIndexTracker tracker,
        IFileSystem fs,
        IOptions<MarkdownRagOptions> options,
        ILogger<MarkdownRagService> logger)
    {
        _chunker = chunker;
        _embeddings = embeddings;
        _vectors = vectors;
        _discovery = discovery;
        _tracker = tracker;
        _fs = fs;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Index markdown documents. When <paramref name="forceReindex"/> is true the entire
    /// collection is dropped and rebuilt; otherwise only changed files are re-embedded and
    /// their prior chunks pruned. Matches <c>index_documents</c> in the original.
    /// </summary>
    /// <param name="progress">Optional callback invoked after each embedding batch to report
    /// progress (current fraction 0..1, total chunks, a message) back to the MCP client via
    /// <c>notifications/progress</c>. Null when the client did not provide a progress token.</param>
    public async Task<IndexResult> IndexDocumentsAsync(
        string currentWorkingDirectory,
        string directory,
        bool recursive,
        bool forceReindex,
        Func<double, double?, string?, CancellationToken, Task>? progress = null,
        CancellationToken ct = default)
    {
        await _indexLock.WaitAsync(ct);
        try
        {
            var targetPath = Path.GetFullPath(Path.Combine(currentWorkingDirectory, directory));
            var storePath = _vectors.GetStoreLocation();
            if (!_fs.Directory.Exists(targetPath))
            {
                return new IndexResult(Message: "Directory does not exist!", ProcessedFiles: 0, TotalChunks: 0, Files: Array.Empty<string>(), Mode: "None", VectorStorePath: storePath);
            }

            IReadOnlyList<string> processedFiles;
            List<ChunkEntity> allChunks;

            if (forceReindex)
            {
                if (_vectors.HasCollection())
                {
                    await _vectors.DropCollectionAsync(ct);
                }
                await _vectors.EnsureCollectionAsync(ct);

                var allFiles = _discovery.ListMarkdownFiles(targetPath, recursive);
                allChunks = await ReadAndChunkAsync(allFiles, ct);
                processedFiles = allFiles;
            }
            else
            {
                var changedFiles = (await _discovery.GetChangedFilesAsync(targetPath, recursive, ct)).ToList();
                if (changedFiles.Count == 0)
                {
                    return new IndexResult(
                        Message: "Already up to date, Nothing to index!",
                        ProcessedFiles: 0, TotalChunks: 0, Files: Array.Empty<string>(), Mode: "Incremental update", VectorStorePath: storePath);
                }

                await _vectors.EnsureCollectionAsync(ct);

                // Prune old chunks for changed files before re-inserting, matching the
                // original's per-file delete(filter=path==...) step.
                await _vectors.DeleteByPathAsync(changedFiles, ct);

                allChunks = await ReadAndChunkAsync(changedFiles, ct);
                processedFiles = changedFiles;
            }

            if (allChunks.Count == 0)
            {
                await _tracker.UpdateAsync(processedFiles, false, ct);
                await _vectors.PersistAsync(ct);
                return new IndexResult(
                    Message: forceReindex ? "Full reindex" : "Incremental update",
                    ProcessedFiles: processedFiles.Count,
                    TotalChunks: 0,
                    Files: processedFiles.Select(f => System.IO.Path.GetFileName(f) ?? f).ToList(),
                    Mode: forceReindex ? "Full reindex" : "Incremental update",
                    VectorStorePath: storePath);
            }

            // Embed in batches to bound peak memory (the original's roadmap item). Report
            // progress after each batch so clients with request timeouts (e.g. the MCP
            // inspector) reset their idle timer and don't cancel long-running corpora.
            var batchSize = Math.Max(1, _options.Indexing.BatchSize);
            var records = new List<(float[] Vector, ChunkEntity Entity)>(allChunks.Count);
            var totalBatches = (int)Math.Ceiling(allChunks.Count / (double)batchSize);
            var batchIndex = 0;
            for (var start = 0; start < allChunks.Count; start += batchSize)
            {
                ct.ThrowIfCancellationRequested();
                var batch = allChunks.Skip(start).Take(batchSize).Select(c => c.Text).ToList();
                var vectors = await _embeddings.EmbedDocumentsAsync(batch, ct);
                for (var i = 0; i < batch.Count; i++)
                {
                    records.Add((vectors[i], allChunks[start + i]));
                }

                if (progress is not null)
                {
                    batchIndex++;
                    var fraction = (double)batchIndex / totalBatches;
                    await progress(
                        fraction,
                        totalBatches,
                        $"Embedded batch {batchIndex}/{totalBatches} ({Math.Min(start + batchSize, allChunks.Count)}/{allChunks.Count} chunks)",
                        ct);
                }
            }

            await _vectors.InsertAsync(records, ct);
            await _tracker.UpdateAsync(processedFiles, false, ct);
            await _vectors.PersistAsync(ct);

            return new IndexResult(
                Message: forceReindex ? "Full reindex" : "Incremental update",
                ProcessedFiles: processedFiles.Count,
                TotalChunks: allChunks.Count,
                Files: processedFiles.Select(f => System.IO.Path.GetFileName(f) ?? f).ToList(),
                Mode: forceReindex ? "Full reindex" : "Incremental update",
                VectorStorePath: storePath);
        }
        finally
        {
            _indexLock.Release();
        }
    }

    /// <summary>
    /// Semantic search. Embeds the query and returns the top-k chunks joined with the same
    /// <c>"---"</c> separators the original <c>search_documents</c> tool emits.
    /// </summary>
    public async Task<string> SearchDocumentsAsync(string query, int? k, CancellationToken ct = default)
    {
        var limit = k ?? _options.Search.DefaultLimit;
        var queryVector = (await _embeddings.EmbedQueriesAsync(new[] { query }, ct))[0];
        var results = await _vectors.SearchAsync(queryVector, limit, ct);

        if (results.Count == 0)
        {
            return "No results found.";
        }

        return string.Join("\n---\n",
            results.Select(res =>
                $"File: **{res.Entity.Filename}**\n---\nText: {res.Entity.Text}\n---\n"));
    }

    /// <summary>
    /// Clear the vector collection and reset the tracking file. Matches <c>clear_index</c>.
    /// </summary>
    public async Task ClearIndexAsync(CancellationToken ct = default)
    {
        await _vectors.EnsureCollectionAsync(ct);
        await _vectors.DeleteAllAsync(ct);
        await _tracker.UpdateAsync(Array.Empty<string>(), clear: true, ct);
        await _vectors.PersistAsync(ct);
    }

    private async Task<List<ChunkEntity>> ReadAndChunkAsync(IReadOnlyList<string> files, CancellationToken ct)
    {
        var chunks = new List<ChunkEntity>();
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (!file.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string text;
            try
            {
                text = await _fs.File.ReadAllTextAsync(file, ct);
            }
            catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Skipping unreadable file {Path}: {Message}", file, ex.Message);
                continue;
            }

            chunks.AddRange(_chunker.Chunk(text, System.IO.Path.GetFileName(file), file));
        }
        return chunks;
    }
}

/// <summary>Structured result of an indexing run, serialized to the MCP client.</summary>
public sealed record IndexResult(
    string Message,
    int ProcessedFiles,
    int TotalChunks,
    IReadOnlyList<string> Files,
    string Mode,
    string? VectorStorePath);
