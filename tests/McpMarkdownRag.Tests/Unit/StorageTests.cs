using System.IO.Abstractions.TestingHelpers;
using MarkdownRagMcp.Configuration;
using MarkdownRagMcp.Domain;
using MarkdownRagMcp.Storage;
using McpMarkdownRag.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace McpMarkdownRag.Tests.Unit;

public class StorageTests
{
    [Fact]
    public async Task JsonIndexTracker_computes_fingerprint_and_persists()
    {
        var fs = new MockFileSystem();
        var testFilePath = @"C:\docs\readme.md";
        fs.AddFile(testFilePath, new MockFileData("# Test Markdown"));

        var options = TestOptions.Create(new MarkdownRagOptions
        {
            DatabasePath = @"C:\db",
            TrackingFileName = "index_tracking.json",
        });

        var tracker = new JsonIndexTracker(fs, options);
        var (hash, mtime) = tracker.ComputeFingerprint(testFilePath);

        Assert.NotEmpty(hash);
        Assert.True(mtime > 0);

        // Update tracking
        await tracker.UpdateAsync([testFilePath], clear: false);

        var loaded = await tracker.LoadAsync();
        Assert.True(loaded.ContainsKey(testFilePath));
        Assert.Equal(hash, loaded[testFilePath].Hash);

        // Clear tracking
        await tracker.UpdateAsync([], clear: true);
        var cleared = await tracker.LoadAsync();
        Assert.Empty(cleared);
    }

    [Fact]
    public async Task MarkdownFileDiscovery_lists_files_and_skips_dot_dirs()
    {
        var fs = new MockFileSystem();
        fs.AddFile(@"C:\repo\doc1.md", new MockFileData("# Doc 1"));
        fs.AddFile(@"C:\repo\sub\doc2.md", new MockFileData("# Doc 2"));
        fs.AddFile(@"C:\repo\.git\ignored.md", new MockFileData("# Git file"));
        fs.AddFile(@"C:\repo\other.txt", new MockFileData("Plain text"));

        var options = TestOptions.Create(new MarkdownRagOptions
        {
            DatabasePath = @"C:\db",
            TrackingFileName = "index_tracking.json",
        });
        var tracker = new JsonIndexTracker(fs, options);
        var discovery = new MarkdownFileDiscovery(fs, tracker, NullLogger<MarkdownFileDiscovery>.Instance);

        // Non-recursive
        var topLevel = discovery.ListMarkdownFiles(@"C:\repo", recursive: false);
        Assert.Single(topLevel);
        Assert.Equal(Path.GetFullPath(@"C:\repo\doc1.md"), topLevel[0]);

        // Recursive: should include doc2.md but exclude .git/ignored.md
        var recursive = discovery.ListMarkdownFiles(@"C:\repo", recursive: true);
        Assert.Equal(2, recursive.Count);
        Assert.Contains(Path.GetFullPath(@"C:\repo\doc1.md"), recursive);
        Assert.Contains(Path.GetFullPath(@"C:\repo\sub\doc2.md"), recursive);
        Assert.DoesNotContain(Path.GetFullPath(@"C:\repo\.git\ignored.md"), recursive);

        // Changed files initially: both are new
        var changed = await discovery.GetChangedFilesAsync(@"C:\repo", recursive: true);
        Assert.Equal(2, changed.Count);

        // Track them
        await tracker.UpdateAsync(changed);

        // No changed files now
        var changedAfter = await discovery.GetChangedFilesAsync(@"C:\repo", recursive: true);
        Assert.Empty(changedAfter);
    }

    [Fact]
    public async Task InMemoryVectorStore_inserts_searches_and_deletes()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mcp_rag_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var options = TestOptions.Create(new MarkdownRagOptions
            {
                DatabasePath = tempDir,
                VectorStoreFileName = "vectors.json",
            });

            var store = new InMemoryVectorStore(options, NullLogger<InMemoryVectorStore>.Instance);
            await store.EnsureCollectionAsync();
            Assert.True(store.HasCollection());

            var v1 = new float[] { 1f, 0f, 0f, 0f };
            var v2 = new float[] { 0f, 1f, 0f, 0f };

            var e1 = new ChunkEntity { Filename = "a.md", Path = "/path/a.md", Text = "Apple text" };
            var e2 = new ChunkEntity { Filename = "b.md", Path = "/path/b.md", Text = "Banana text" };

            await store.InsertAsync([(v1, e1), (v2, e2)]);

            // Search query close to v1
            var hits = await store.SearchAsync(new float[] { 0.9f, 0.1f, 0f, 0f }, k: 2);
            Assert.Equal(2, hits.Count);
            Assert.Equal("a.md", hits[0].Entity.Filename);

            // Delete by path
            await store.DeleteByPathAsync(["/path/a.md"]);
            var hitsAfterDelete = await store.SearchAsync(new float[] { 1f, 0f, 0f, 0f }, k: 2);
            Assert.Single(hitsAfterDelete);
            Assert.Equal("b.md", hitsAfterDelete[0].Entity.Filename);

            // Drop collection
            await store.DropCollectionAsync();
            Assert.False(store.HasCollection());
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
