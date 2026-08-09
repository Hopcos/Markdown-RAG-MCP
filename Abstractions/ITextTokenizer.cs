namespace MarkdownRagMcp.Abstractions;

/// <summary>
/// Minimal tokenizer contract used by the chunker to approximate token counts.
/// The default implementation delegates to the BERT tokenizer bundled with the
/// embedding provider, keeping the chunker decoupled from any specific tokenizer library.
/// </summary>
public interface ITextTokenizer
{
    /// <summary>Approximate number of tokens in <paramref name="text"/> (no truncation).</summary>
    int CountTokens(string text);
}
