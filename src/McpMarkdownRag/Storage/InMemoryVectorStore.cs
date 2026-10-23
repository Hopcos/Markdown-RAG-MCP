using System.Collections.Concurrent;
using System.Text.Json;
using MarkdownRagMcp.Abstractions;
using MarkdownRagMcp.Configuration;
using MarkdownRagMcp.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarkdownRagMcp.Storage;

/// <summary>
/// Default local-first vector store. Holds records in memory and persists them as JSON,
/// mirroring the file-based Milvus Lite usage of the original project. Cosine similarity
/// is computed directly (vectors are unit-normalized by the embedding provider, so dot
/// product == cosine). The abstraction lets a real Milvus client be substituted via DI.
/// </summary>
public sealed class InMemoryVectorStore : IVectorStore, IDisposable
{
    private readonly MarkdownRagOptions _options;
    private readonly ILogger<InMemoryVectorStore> _logger;
    private readonly ConcurrentDictionary<long, VectorRecord> _records = new();
    private readonly object _idLock = new();
    private long _nextId = 1;
    private bool _loaded;
    private bool _exists;

    private string PersistPath => Path.GetFullPath(
        Path.Combine(_options.DatabasePath, _options.VectorStoreFileName));

    public InMemoryVectorStore(IOptions<MarkdownRagOptions> options, ILogger<InMemoryVectorStore> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public bool HasCollection()
    {
        EnsureLoaded();
        return _exists;
    }

    public Task EnsureCollectionAsync(CancellationToken ct = default)
    {
        EnsureLoaded();
        _exists = true;
        return Task.CompletedTask;
    }

    public async Task DropCollectionAsync(CancellationToken ct = default)
    {
        _records.Clear();
        lock (_idLock) _nextId = 1;
        _exists = false;
        if (File.Exists(PersistPath))
        {
            File.Delete(PersistPath);
        }
        await Task.CompletedTask;
    }

    public Task InsertAsync(IReadOnlyList<(float[] Vector, ChunkEntity Entity)> records, CancellationToken ct = default)
    {
        EnsureLoaded();
        foreach (var (vector, entity) in records)
        {
            long id;
            lock (_idLock) id = _nextId++;
            _records[id] = new VectorRecord { Id = id, Vector = vector, Entity = entity };
        }
        _exists = true;
        return Task.CompletedTask;
    }

    public Task DeleteByPathAsync(IReadOnlyCollection<string> filePaths, CancellationToken ct = default)
    {
        EnsureLoaded();
        var set = new HashSet<string>(filePaths);
        var toRemove = _records.Where(kv => set.Contains(kv.Value.Entity.Path)).Select(kv => kv.Key).ToList();
        foreach (var id in toRemove)
        {
            _records.TryRemove(id, out _);
        }
        return Task.CompletedTask;
    }

    public Task DeleteAllAsync(CancellationToken ct = default)
    {
        _records.Clear();
        lock (_idLock) _nextId = 1;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SearchResult>> SearchAsync(float[] queryVector, int k, CancellationToken ct = default)
    {
        EnsureLoaded();
        if (_records.IsEmpty)
        {
            return Task.FromResult<IReadOnlyList<SearchResult>>(Array.Empty<SearchResult>());
        }

        // Cosine distance = 1 - cosine similarity. The original returns Milvus "distance"
        // (lower is better); we preserve that convention so result ordering matches.
        var scored = _records.Values
            .Select(r => new
            {
                r,
                sim = DotProduct(r.Vector, queryVector),
            })
            .OrderByDescending(x => x.sim)
            .Take(k)
            .Select(x => new SearchResult(Distance: 1f - x.sim, Entity: x.r.Entity))
            .ToList();

        return Task.FromResult<IReadOnlyList<SearchResult>>(scored);
    }

    public async Task PersistAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(_options.DatabasePath);
        var payload = _records.Values.OrderBy(r => r.Id).ToList();
        await using var stream = File.Create(PersistPath);
        await JsonSerializer.SerializeAsync(stream, payload, JsonOptions, ct);
    }

    public string? GetStoreLocation() => PersistPath;

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;

        if (!File.Exists(PersistPath))
        {
            return;
        }

        try
        {
            using var stream = File.OpenRead(PersistPath);
            var records = JsonSerializer.DeserializeAsync<List<VectorRecord>>(stream, JsonOptions).GetAwaiter().GetResult();
            if (records is null) return;
            foreach (var r in records)
            {
                _records[r.Id] = r;
                lock (_idLock)
                {
                    if (r.Id >= _nextId) _nextId = r.Id + 1;
                }
            }
            _exists = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load persisted vector store at {Path}; starting empty.", PersistPath);
        }
    }

    private static float DotProduct(float[] a, float[] b)
    {
        float sum = 0;
        for (var i = 0; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }
        return sum;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    public void Dispose()
    {
        // Best-effort flush on disposal so a graceful shutdown preserves the index.
        // CRITICAL: only flush when the in-memory store actually holds records. The store
        // loads lazily and marks itself loaded on first use; a process that was started
        // (or had its first request) before vectors.json was in place holds an EMPTY store
        // even though a valid index exists on disk. Unconditional PersistAsync would
        // overwrite that on-disk index with an empty array on every recycle — silently
        // destroying data. Clearing is an explicit operation (ClearIndexAsync persists the
        // empty state itself), so an empty in-memory store must never write back.
        if (!_records.IsEmpty)
        {
            try { PersistAsync().GetAwaiter().GetResult(); }
            catch { /* swallow; disposal must not throw */ }
        }
    }
}
