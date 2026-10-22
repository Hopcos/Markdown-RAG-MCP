using System.Collections.Concurrent;
using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text.Json;
using MarkdownRagMcp.Abstractions;
using MarkdownRagMcp.Configuration;
using Microsoft.Extensions.Options;

namespace MarkdownRagMcp.Storage;

/// <summary>
/// JSON-backed index tracker. Stores an md5 hash + last-write-time per file path, exactly
/// like the original <c>index_tracking.json</c>. Uses <see cref="IFileSystem"/> for
/// testability and treats missing/locked files as deletions to be pruned.
/// </summary>
public sealed class JsonIndexTracker : IIndexTracker
{
    private readonly IFileSystem _fs;
    private readonly MarkdownRagOptions _options;
    private readonly ConcurrentDictionary<string, FileFingerprint> _cache = new(StringComparer.Ordinal);
    private bool _loaded;

    private string TrackingPath => Path.GetFullPath(
        Path.Combine(_options.DatabasePath, _options.TrackingFileName));

    public JsonIndexTracker(IFileSystem fs, IOptions<MarkdownRagOptions> options)
    {
        _fs = fs;
        _options = options.Value;
    }

    public Task<IReadOnlyDictionary<string, FileFingerprint>> LoadAsync(CancellationToken ct = default)
    {
        EnsureLoaded();
        return Task.FromResult<IReadOnlyDictionary<string, FileFingerprint>>(_cache);
    }

    public async Task UpdateAsync(IReadOnlyList<string> filePaths, bool clear = false, CancellationToken ct = default)
    {
        EnsureLoaded();

        if (clear)
        {
            _cache.Clear();
            await SaveAsync(ct);
            return;
        }

        foreach (var path in filePaths)
        {
            try
            {
                var (hash, mtime) = ComputeFingerprint(path);
                _cache[path] = new FileFingerprint { Hash = hash, ModifiedTicks = mtime };
            }
            catch (FileNotFoundException)
            {
                _cache.TryRemove(path, out _);
            }
            catch (UnauthorizedAccessException)
            {
                _cache.TryRemove(path, out _);
            }
        }

        await SaveAsync(ct);
    }

    public (string Hash, long ModifiedTicks) ComputeFingerprint(string filePath)
    {
        using var stream = _fs.File.OpenRead(filePath);
        var hash = MD5.HashData(stream);
        var modified = _fs.File.GetLastWriteTimeUtc(filePath).Ticks;
        return (Convert.ToHexString(hash).ToLowerInvariant(), modified);
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;

        if (!_fs.File.Exists(TrackingPath))
        {
            return;
        }

        try
        {
            using var stream = _fs.File.OpenRead(TrackingPath);
            var data = JsonSerializer.DeserializeAsync<Dictionary<string, FileFingerprint>>(stream).GetAwaiter().GetResult();
            if (data is null) return;
            foreach (var kv in data)
            {
                _cache[kv.Key] = kv.Value;
            }
        }
        catch
        {
            // Corrupt tracking file: start fresh rather than crashing the server.
        }
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        _fs.Directory.CreateDirectory(_options.DatabasePath);
        await using var stream = _fs.File.Create(TrackingPath);
        await JsonSerializer.SerializeAsync(stream, _cache, JsonOptions, ct);
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
}
