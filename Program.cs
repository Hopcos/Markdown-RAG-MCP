using System.IO.Abstractions;
using MarkdownRagMcp.Abstractions;
using MarkdownRagMcfg = MarkdownRagMcp.Configuration;
using MarkdownRagMcp.Chunking;
using MarkdownRagMcp.Embeddings;
using MarkdownRagMcp.Services;
using MarkdownRagMcp.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

// MCP servers speak JSON-RPC over stdin/stdout, so all logging is redirected to stderr
// to avoid corrupting the protocol stream.
var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services
    .AddOptions<MarkdownRagMcfg.MarkdownRagOptions>()
    .Bind(builder.Configuration.GetSection(MarkdownRagMcfg.MarkdownRagOptions.SectionName));

// File-system abstraction (testable; default is the real filesystem).
builder.Services.AddSingleton<IFileSystem, FileSystem>();

// Index tracking & file discovery.
builder.Services.AddSingleton<IIndexTracker, JsonIndexTracker>();
builder.Services.AddSingleton<IMarkdownFileDiscovery, MarkdownFileDiscovery>();

// Chunker + tokenizer (shared with the embedding provider so both use the model vocab).
builder.Services.AddSingleton<LazyBertTokenizer>();
builder.Services.AddSingleton<ITextTokenizer>(sp => sp.GetRequiredService<LazyBertTokenizer>());
builder.Services.AddSingleton<IMarkdownChunker, MarkdownChunker>();

// Embedding provider + first-run model downloader.
builder.Services.AddHttpClient<HuggingFaceModelDownloader>();
// Registered as both the interface and the concrete type: the preload hosted service needs
// the concrete type to call EnsureLoadedAsync (a non-interface method).
builder.Services.AddSingleton<OnnxEmbeddingProvider>();
builder.Services.AddSingleton<IEmbeddingProvider>(sp => sp.GetRequiredService<OnnxEmbeddingProvider>());

// Vector store (local-first in-memory, JSON-persisted).
builder.Services.AddSingleton<IVectorStore, InMemoryVectorStore>();

// Orchestration service consumed by the MCP tools.
builder.Services.AddSingleton<MarkdownRagService>();

// Preload the ONNX model in the background at startup so the first tool call doesn't pay
// the download + session-load cost inside the MCP request path (which can exceed a client's
// request timeout). Fire-and-forget; tool calls await the load if it's still in progress.
builder.Services.AddHostedService<EmbeddingPreloadHostedService>();

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new() { Name = "mcp-markdown-rag", Version = "0.2.0" };
        options.ServerInstructions = """
            This MCP server provides semantic search capabilities over markdown files using
            vector embeddings and a local vector database. It enables you to index markdown
            documents and perform intelligent searches to find relevant content based on
            semantic similarity rather than just keyword matching.
            """;
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
