namespace MarkdownRagMcp.Configuration;

/// <summary>
/// All runtime configuration for the Markdown RAG MCP server.
/// Everything is fixed in appsettings.json — there is no command-line surface.
/// </summary>
public sealed class MarkdownRagOptions
{
    public const string SectionName = "MarkdownRag";

    /// <summary>
    /// Which MCP transport to expose: "Stdio" (default, for child-process hosts like
    /// Claude Desktop/Cursor) or "Http" (Streamable HTTP, for network hosts).
    /// </summary>
    public string Transport { get; set; } = "Stdio";

    public TransportOptions TransportSettings { get; set; } = new();

    /// <summary>Directory holding the tracking file and persisted vector store.</summary>
    public string DatabasePath { get; set; } = "./.db";

    /// <summary>Logical name of the vector collection (mirrors the Milvus collection name).</summary>
    public string CollectionName { get; set; } = "markdown_vectors";

    /// <summary>Name of the JSON file tracking indexed-file hashes and modified times.</summary>
    public string TrackingFileName { get; set; } = "index_tracking.json";

    /// <summary>Name of the JSON file the in-memory vector store persists to.</summary>
    public string VectorStoreFileName { get; set; } = "vectors.json";

    public EmbeddingOptions Embedding { get; set; } = new();
    public ChunkingOptions Chunking { get; set; } = new();
    public SearchOptions Search { get; set; } = new();
    public IndexingOptions Indexing { get; set; } = new();
}

public sealed class EmbeddingOptions
{
    /// <summary>Provider key resolved from DI: "Onnx" (local default) or future implementations.</summary>
    public string Provider { get; set; } = "Onnx";

    /// <summary>Dimensionality of the embedding vectors. Must match the configured model.</summary>
    public int Dimension { get; set; } = 768;

    /// <summary>Hugging Face model id used to locate and cache the ONNX + vocab artifacts.</summary>
    public string ModelId { get; set; } = "sentence-transformers/all-MiniLM-L6-v2";

    /// <summary>Directory where model artifacts are cached after first download.</summary>
    public string ModelCachePath { get; set; } = "./.models/embeddings";

    /// <summary>Maximum token sequence length fed to the model; longer input is truncated.</summary>
    public int MaxSequenceLength { get; set; } = 256;

    /// <summary>Pooling strategy applied to token embeddings: "Mean" or "Cls".</summary>
    public string PoolingMode { get; set; } = "Mean";
}

public sealed class ChunkingOptions
{
    /// <summary>Target token chunk size for the secondary token splitter.</summary>
    public int ChunkSize { get; set; } = 512;

    /// <summary>Token overlap between consecutive chunks to preserve context continuity.</summary>
    public int ChunkOverlap { get; set; } = 100;
}

public sealed class SearchOptions
{
    /// <summary>Default number of results returned when the caller omits the limit argument.</summary>
    public int DefaultLimit { get; set; } = 5;
}

public sealed class IndexingOptions
{
    /// <summary>Number of documents embedded per batch to balance CPU and memory use.</summary>
    public int BatchSize { get; set; } = 32;
}

public sealed class TransportOptions
{
    /// <summary>URL prefix the Streamable HTTP server binds to when Transport is "Http".</summary>
    public string HttpEndpointUrl { get; set; } = "http://localhost:3001";

    /// <summary>Route prefix for the MCP HTTP endpoint (empty means the root "/").</summary>
    public string HttpRoutePrefix { get; set; } = "";

    /// <summary>
    /// Stateless mode (recommended for servers that don't issue server-to-client requests
    /// like sampling/elicitation). Set false only if a client needs server-initiated requests.
    /// </summary>
    public bool Stateless { get; set; } = true;
}
