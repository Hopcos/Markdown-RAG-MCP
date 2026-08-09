using System.IO.Abstractions;
using MarkdownRagMcp.Abstractions;
using Microsoft.Extensions.Logging;

namespace MarkdownRagMcp.Storage;

/// <summary>
/// Discovers <c>.md</c> files under a directory. Skips dot-prefixed directories in
/// recursive mode, matching the original <c>list_md_files</c> behavior. Uses
/// <see cref="IFileSystem"/> so the discovery is testable against an in-memory FS.
/// </summary>
public sealed class MarkdownFileDiscovery : IMarkdownFileDiscovery
{
    private readonly IFileSystem _fs;
    private readonly IIndexTracker _tracker;
    private readonly ILogger<MarkdownFileDiscovery> _logger;

    public MarkdownFileDiscovery(
        IFileSystem fs,
        IIndexTracker tracker,
        ILogger<MarkdownFileDiscovery> logger)
    {
        _fs = fs;
        _tracker = tracker;
        _logger = logger;
    }

    public IReadOnlyList<string> ListMarkdownFiles(string directory, bool recursive)
    {
        if (!_fs.Directory.Exists(directory))
        {
            return Array.Empty<string>();
        }

        var result = new List<string>();
        var option = recursive
            ? EnumerationOptionsCompat.RecurseSkipDotDirs
            : EnumerationOptionsCompat.TopLevelOnly;

        foreach (var path in _fs.Directory.EnumerateFiles(directory, "*.md", option))
        {
            // EnumerateFiles(*.md) is case-insensitive on Windows but case-sensitive on
            // Linux; normalize so behavior matches across platforms.
            if (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(Path.GetFullPath(path));
            }
        }
        return result;
    }

    public async Task<IReadOnlyList<string>> GetChangedFilesAsync(string directory, bool recursive, CancellationToken ct = default)
    {
        var tracking = await _tracker.LoadAsync(ct);
        var mdFiles = ListMarkdownFiles(directory, recursive);

        var changed = new List<string>();
        var livePaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in mdFiles)
        {
            livePaths.Add(file);
            if (!tracking.TryGetValue(file, out var stored))
            {
                changed.Add(file);
                continue;
            }

            try
            {
                var (hash, mtime) = _tracker.ComputeFingerprint(file);
                if (!string.Equals(hash, stored.Hash, StringComparison.Ordinal) || mtime != stored.ModifiedTicks)
                {
                    changed.Add(file);
                }
            }
            catch (FileNotFoundException)
            {
                _logger.LogDebug("File vanished during change scan: {Path}", file);
            }
            catch (UnauthorizedAccessException)
            {
                _logger.LogWarning("Permission denied reading fingerprint for {Path}; treating as unchanged.", file);
            }
        }

        return changed;
    }

    private static class EnumerationOptionsCompat
    {
        // Recursive: descend into subdirectories but skip any whose name starts with '.'.
        public static EnumerationOptions RecurseSkipDotDirs => new()
        {
            RecurseSubdirectories = true,
            // IgnoreInaccessible true keeps walking past lock files / system dirs.
            IgnoreInaccessible = true,
        };

        public static EnumerationOptions TopLevelOnly => new()
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
        };
    }
}
