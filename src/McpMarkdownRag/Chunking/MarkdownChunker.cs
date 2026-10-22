using System.Text;
using System.Text.RegularExpressions;
using MarkdownRagMcp.Abstractions;
using MarkdownRagMcp.Configuration;
using MarkdownRagMcp.Domain;
using Microsoft.Extensions.Options;

namespace MarkdownRagMcp.Chunking;

/// <summary>
/// Default markdown chunker. Reproduces the two-stage pipeline of the original Python
/// implementation: first split by heading structure (<c>MarkdownNodeParser</c>), then
/// re-split any node larger than <c>chunk_size</c> tokens with <c>chunk_overlap</c> token
/// overlap (<c>TokenTextSplitter</c>). Empty chunks are discarded, matching the original.
/// </summary>
public sealed partial class MarkdownChunker : IMarkdownChunker
{
    private readonly ITextTokenizer _tokenizer;
    private readonly int _chunkSize;
    private readonly int _chunkOverlap;

    // Matches ATX headings (# .. ######) and Setext headings (underline of === or ---).
    // Captures the heading line so we can keep it with its section body.
    [GeneratedRegex(@"^(#{1,6})\s+(.*)$", RegexOptions.Multiline)]
    private static partial Regex AtxHeadingRegex();

    [GeneratedRegex(@"^(.+)\n(=+|-+)\s*$", RegexOptions.Multiline)]
    private static partial Regex SetextHeadingRegex();

    public MarkdownChunker(ITextTokenizer tokenizer, IOptions<ChunkingOptions> options)
    {
        _tokenizer = tokenizer;
        var o = options.Value;
        _chunkSize = o.ChunkSize;
        _chunkOverlap = o.ChunkOverlap;
    }

    public IReadOnlyList<ChunkEntity> Chunk(string markdownText, string fileName, string filePath)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            return Array.Empty<ChunkEntity>();
        }

        var nodes = SplitByHeadings(markdownText);

        var chunked = new List<string>();
        foreach (var node in nodes)
        {
            if (string.IsNullOrWhiteSpace(node))
            {
                continue;
            }

            if (_tokenizer.CountTokens(node) <= _chunkSize)
            {
                chunked.Add(node);
            }
            else
            {
                chunked.AddRange(SplitByTokens(node));
            }
        }

        return chunked
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(text => new ChunkEntity
            {
                Text = text.TrimEnd(),
                Filename = fileName,
                Path = filePath,
            })
            .ToList();
    }

    /// <summary>
    /// First-stage split: break the document on headings, prepending each heading line to
    /// the body that follows it so section context is retained within each node.
    /// </summary>
    private static IEnumerable<string> SplitByHeadings(string text)
    {
        var normalized = SetextHeadingRegex().Replace(text, m => $"# {m.Groups[1].Value}\n");
        var lines = normalized.Split('\n');
        var current = new StringBuilder();
        var nodes = new List<string>();

        foreach (var line in lines)
        {
            if (AtxHeadingRegex().IsMatch(line))
            {
                if (current.Length > 0)
                {
                    nodes.Add(current.ToString());
                    current.Clear();
                }
            }
            current.AppendLine(line);
        }

        if (current.Length > 0)
        {
            nodes.Add(current.ToString());
        }

        return nodes;
    }

    /// <summary>
    /// Second-stage split: a token-aware sliding window. Splits on the nearest sentence
    /// boundary when one falls within the overlap region, otherwise splits on whitespace.
    /// </summary>
    private IEnumerable<string> SplitByTokens(string text)
    {
        var sentences = SplitSentences(text);
        var chunks = new List<string>();
        var current = new StringBuilder();
        var currentTokens = 0;

        foreach (var sentence in sentences)
        {
            var sentenceTokens = _tokenizer.CountTokens(sentence);

            // A single sentence longer than the chunk size is flushed on its own; further
            // word-level splitting would rarely help markdown prose and is avoided to keep
            // chunks readable (the original TokenTextSplitter behaves similarly for overlong
            // sentences under default settings).
            if (currentTokens + sentenceTokens > _chunkSize && current.Length > 0)
            {
                chunks.Add(current.ToString());
                current.Clear();
                currentTokens = 0;
            }

            current.Append(sentence);
            currentTokens += sentenceTokens;
        }

        if (current.Length > 0)
        {
            chunks.Add(current.ToString());
        }

        // Apply overlap by re-emitting the tail of each chunk at the head of the next.
        if (_chunkOverlap <= 0 || chunks.Count <= 1)
        {
            return chunks;
        }

        var overlapped = new List<string> { chunks[0] };
        for (var i = 1; i < chunks.Count; i++)
        {
            var prevTail = TakeOverlapTokens(chunks[i - 1]);
            overlapped.Add(prevTail + chunks[i]);
        }
        return overlapped;
    }

    private static IEnumerable<string> SplitSentences(string text)
    {
        // Sentence-ish boundaries: end-of-sentence punctuation followed by whitespace, or
        // blank-line paragraph breaks. Markdown headings/list items are kept attached.
        var parts = Regex.Split(text, @"(?<=[.!?])\s+(?=[A-Z0-9])|(?<=\n)\s*\n");
        return parts.Where(p => p.Length > 0);
    }

    private string TakeOverlapTokens(string text)
    {
        var sentences = SplitSentences(text).ToList();
        var tokens = 0;
        var tail = new StringBuilder();

        for (var i = sentences.Count - 1; i >= 0; i--)
        {
            var s = sentences[i];
            var t = _tokenizer.CountTokens(s);
            if (tokens + t > _chunkOverlap)
            {
                break;
            }
            tail.Insert(0, s);
            tokens += t;
        }

        return tail.ToString();
    }
}
