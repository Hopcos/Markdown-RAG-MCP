namespace MarkdownRagMcp.Abstractions;

/// <summary>
/// Discovers markdown files under a directory and, combined with an
/// <see cref="IIndexTracker"/>, reports which files have changed since the last indexing.
/// </summary>
public interface IMarkdownFileDiscovery
{
    /// <summary>List all <c>.md</c> files under <paramref name="directory"/>.</summary>
    IReadOnlyList<string> ListMarkdownFiles(string directory, bool recursive);

    /// <summary>Return files that are new or whose fingerprint differs from the tracker.</summary>
    Task<IReadOnlyList<string>> GetChangedFilesAsync(string directory, bool recursive, CancellationToken ct = default);
}
