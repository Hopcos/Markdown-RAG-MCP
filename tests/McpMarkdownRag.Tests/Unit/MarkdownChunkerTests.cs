using MarkdownRagMcp.Chunking;
using MarkdownRagMcp.Configuration;
using McpMarkdownRag.Tests.Support;
using Microsoft.Extensions.Options;

namespace McpMarkdownRag.Tests.Unit;

public class MarkdownChunkerTests
{
    [Fact]
    public void Chunk_empty_or_whitespace_returns_empty_list()
    {
        var tokenizer = new SimpleTestTokenizer();
        var options = TestOptions.Create(new ChunkingOptions { ChunkSize = 50, ChunkOverlap = 10 });
        var chunker = new MarkdownChunker(tokenizer, options);

        var result = chunker.Chunk("", "test.md", "/path/test.md");
        Assert.Empty(result);

        var whitespaceResult = chunker.Chunk("   \n\t  ", "test.md", "/path/test.md");
        Assert.Empty(whitespaceResult);
    }

    [Fact]
    public void Chunk_splits_by_headings_correctly()
    {
        var tokenizer = new SimpleTestTokenizer();
        var options = TestOptions.Create(new ChunkingOptions { ChunkSize = 100, ChunkOverlap = 10 });
        var chunker = new MarkdownChunker(tokenizer, options);

        var markdown = """
            # Introduction
            This is the intro section with some words.

            ## Details
            Here are the details for the architecture.

            ### Sub-details
            Even more details here.
            """;

        var result = chunker.Chunk(markdown, "doc.md", "/docs/doc.md");

        Assert.NotEmpty(result);
        Assert.All(result, chunk =>
        {
            Assert.Equal("doc.md", chunk.Filename);
            Assert.Equal("/docs/doc.md", chunk.Path);
            Assert.False(string.IsNullOrWhiteSpace(chunk.Text));
        });

        Assert.Contains(result, c => c.Text.Contains("# Introduction"));
        Assert.Contains(result, c => c.Text.Contains("## Details"));
        Assert.Contains(result, c => c.Text.Contains("### Sub-details"));
    }

    [Fact]
    public void Chunk_splits_oversized_sections_with_overlap()
    {
        var tokenizer = new SimpleTestTokenizer();
        // Tiny chunk size of 5 words with 2 words overlap; three short sentences force the
        // sliding window to flush between sentences and re-emit overlapping tails.
        var options = TestOptions.Create(new ChunkingOptions { ChunkSize = 5, ChunkOverlap = 2 });
        var chunker = new MarkdownChunker(tokenizer, options);

        var markdown = """
            # Oversized
            Word one two three. Four five six. Seven eight nine.
            """;

        var result = chunker.Chunk(markdown, "large.md", "/docs/large.md");

        Assert.True(result.Count > 1, $"Expected multiple chunks, got {result.Count}");
        Assert.All(result, c => Assert.Equal("large.md", c.Filename));
    }
}
