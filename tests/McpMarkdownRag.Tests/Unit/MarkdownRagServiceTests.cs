using System.IO.Abstractions.TestingHelpers;
using MarkdownRagMcp.Abstractions;
using MarkdownRagMcp.Chunking;
using MarkdownRagMcp.Configuration;
using MarkdownRagMcp.Services;
using MarkdownRagMcp.Storage;
using McpMarkdownRag.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace McpMarkdownRag.Tests.Unit;

public class MarkdownRagServiceTests
{
    [Fact]
    public async Task IndexDocumentsAsync_handles_nonexistent_directory()
    {
        var fs = new MockFileSystem();
        var tracker = new JsonIndexTracker(fs, TestOptions.Create(new MarkdownRagOptions()));
        var discovery = new MarkdownFileDiscovery(fs, tracker, NullLogger<MarkdownFileDiscovery>.Instance);
        var chunker = new MarkdownChunker(new SimpleTestTokenizer(), TestOptions.Create(new ChunkingOptions()));
        var embeddings = new FakeEmbeddingProvider();
        var tempDb = Path.Combine(Path.GetTempPath(), "rag_svc_test_" + Guid.NewGuid().ToString("N"));
        var vectorStore = new InMemoryVectorStore(TestOptions.Create(new MarkdownRagOptions { DatabasePath = tempDb }), NullLogger<InMemoryVectorStore>.Instance);

        var service = new MarkdownRagService(
            chunker, embeddings, vectorStore, discovery, tracker, fs,
            TestOptions.Create(new MarkdownRagOptions { DatabasePath = tempDb }),
            NullLogger<MarkdownRagService>.Instance);

        var res = await service.IndexDocumentsAsync(@"C:\nonexistent", "", false, false);
        Assert.Equal("Directory does not exist!", res.Message);
        Assert.Equal(0, res.ProcessedFiles);
    }

    [Fact]
    public async Task Full_lifecycle_index_search_incremental_clear()
    {
        var tempDb = Path.Combine(Path.GetTempPath(), "rag_lifecycle_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDb);
        try
        {
            var fs = new MockFileSystem();
            var docDir = @"C:\work\docs";
            fs.AddDirectory(docDir);
            fs.AddFile(Path.Combine(docDir, "arch.md"), new MockFileData("# System Architecture\nThis describes the core architecture."));
            fs.AddFile(Path.Combine(docDir, "api.md"), new MockFileData("# API Reference\nThis outlines all HTTP endpoints."));

            var options = TestOptions.Create(new MarkdownRagOptions
            {
                DatabasePath = tempDb,
                TrackingFileName = "track.json",
                VectorStoreFileName = "vectors.json",
                Indexing = new IndexingOptions { BatchSize = 10 },
                Search = new SearchOptions { DefaultLimit = 5 }
            });

            var tracker = new JsonIndexTracker(fs, options);
            var discovery = new MarkdownFileDiscovery(fs, tracker, NullLogger<MarkdownFileDiscovery>.Instance);
            var chunker = new MarkdownChunker(new SimpleTestTokenizer(), TestOptions.Create(new ChunkingOptions { ChunkSize = 100, ChunkOverlap = 10 }));
            var embeddings = new FakeEmbeddingProvider();
            var vectorStore = new InMemoryVectorStore(options, NullLogger<InMemoryVectorStore>.Instance);

            var service = new MarkdownRagService(
                chunker, embeddings, vectorStore, discovery, tracker, fs,
                options, NullLogger<MarkdownRagService>.Instance);

            // 1. Initial Indexing
            var indexRes = await service.IndexDocumentsAsync(@"C:\work", "docs", recursive: false, forceReindex: false);
            Assert.Equal("Incremental update", indexRes.Message);
            Assert.Equal(2, indexRes.ProcessedFiles);
            Assert.True(indexRes.TotalChunks >= 2);

            // 2. Incremental again without changes -> Nothing to index
            var indexAgain = await service.IndexDocumentsAsync(@"C:\work", "docs", recursive: false, forceReindex: false);
            Assert.Equal("Already up to date, Nothing to index!", indexAgain.Message);
            Assert.Equal(0, indexAgain.ProcessedFiles);

            // 3. Search
            var searchRes = await service.SearchDocumentsAsync("Architecture", k: 2);
            Assert.Contains("arch.md", searchRes);
            Assert.Contains("---", searchRes);

            // 4. Force reindex
            var forceRes = await service.IndexDocumentsAsync(@"C:\work", "docs", recursive: false, forceReindex: true);
            Assert.Equal("Full reindex", forceRes.Message);
            Assert.Equal(2, forceRes.ProcessedFiles);

            // 5. Clear index
            await service.ClearIndexAsync();
            var searchAfterClear = await service.SearchDocumentsAsync("Architecture", k: 2);
            Assert.Equal("No results found.", searchAfterClear);
        }
        finally
        {
            if (Directory.Exists(tempDb))
            {
                Directory.Delete(tempDb, recursive: true);
            }
        }
    }
}
