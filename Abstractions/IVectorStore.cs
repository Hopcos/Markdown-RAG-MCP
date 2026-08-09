using MarkdownRagMcp.Domain;

namespace MarkdownRagMcp.Abstractions;

/// <summary>
/// Persistence and similarity search over a collection of vectorized chunks.
/// The default implementation is an in-memory store persisted to JSON, mirroring the
/// local file-based Milvus Lite usage of the original. The abstraction allows a real
/// Milvus or other vector DB to be substituted via DI.
/// </summary>
public interface IVectorStore
{
    /// <summary>True when the named collection already exists.</summary>
    bool HasCollection();

    /// <summary>Create an empty collection if one does not already exist.</summary>
    Task EnsureCollectionAsync(CancellationToken ct = default);

    /// <summary>Drop the collection and all of its records.</summary>
    Task DropCollectionAsync(CancellationToken ct = default);

    /// <summary>Insert a batch of records, each carrying its own vector and source metadata.</summary>
    Task InsertAsync(IReadOnlyList<(float[] Vector, ChunkEntity Entity)> records, CancellationToken ct = default);

    /// <summary>Delete every record whose <c>Path</c> equals one of <paramref name="filePaths"/>.</summary>
    Task DeleteByPathAsync(IReadOnlyCollection<string> filePaths, CancellationToken ct = default);

    /// <summary>Delete every record (filter <c>id &gt;= 0</c> in the original).</summary>
    Task DeleteAllAsync(CancellationToken ct = default);

    /// <summary>Return the <paramref name="k"/> nearest chunks to <paramref name="queryVector"/>.</summary>
    Task<IReadOnlyList<SearchResult>> SearchAsync(float[] queryVector, int k, CancellationToken ct = default);

    /// <summary>Flush the in-memory state to disk (no-op for stores that persist eagerly).</summary>
    Task PersistAsync(CancellationToken ct = default);

    /// <summary>
    /// Absolute path to the persisted store file (e.g. <c>vectors.json</c>), or null when the
    /// store has no on-disk representation. Returned to MCP clients so they can locate the index.
    /// </summary>
    string? GetStoreLocation();
}

/// <summary>
/// Internal record persisted by the default in-memory store. Kept separate from
/// <see cref="ChunkEntity"/> so the storage layout can evolve independently of the API model.
/// </summary>
public sealed class VectorRecord
{
    public long Id { get; set; }
    public required float[] Vector { get; set; }
    public required ChunkEntity Entity { get; set; }
}
