namespace MarkdownRagMcp.Abstractions;

/// <summary>
/// Tracks which source files have been indexed, keyed by absolute path, so that
/// incremental indexing can skip unchanged files and prune deleted ones.
/// Mirrors <c>index_tracking.json</c> in the original implementation.
/// </summary>
public interface IIndexTracker
{
    /// <summary>Load the persisted tracking map (empty when none exists yet).</summary>
    Task<IReadOnlyDictionary<string, FileFingerprint>> LoadAsync(CancellationToken ct = default);

    /// <summary>
    /// Refresh fingerprints for the given files, dropping any that no longer exist,
    /// and persist the result. When <paramref name="clear"/> is true, write an empty map.
    /// </summary>
    Task UpdateAsync(IReadOnlyList<string> filePaths, bool clear = false, CancellationToken ct = default);

    /// <summary>Compute the (md5 hash, modified-time) fingerprint of a file.</summary>
    (string Hash, long ModifiedTicks) ComputeFingerprint(string filePath);
}

/// <summary>Stored fingerprint of one indexed file.</summary>
public sealed class FileFingerprint
{
    public string Hash { get; set; } = string.Empty;
    public long ModifiedTicks { get; set; }
}
