using System.IO.Abstractions;
using MarkdownRagMcp.Abstractions;
using MarkdownRagMcfg = MarkdownRagMcp.Configuration;
using MarkdownRagMcp.Chunking;
using MarkdownRagMcp.Embeddings;
using MarkdownRagMcp.Security;
using MarkdownRagMcp.Services;
using MarkdownRagMcp.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;

// All shared services (file system, embedding pipeline, vector store, orchestration, preload)
// are wired once in AddMarkdownRagServices, so the stdio and HTTP hosts are configured
// identically. Only the transport and host type differ.

var transport = builder_GetTransport(args);

if (transport == "Http")
{
    await RunHttpAsync(args);
}
else
{
    await RunStdioAsync(args);
}
return;

// ---------------------------------------------------------------------------
// Stdio host: MCP speaks JSON-RPC over stdin/stdout, so all logging is
// redirected to stderr to avoid corrupting the protocol stream.
// ---------------------------------------------------------------------------
static async Task RunStdioAsync(string[] args)
{
    var builder = Host.CreateApplicationBuilder(args);

    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

    AddMarkdownRagServices(builder.Services, builder.Configuration);

    builder.Services
        .AddMcpServer(ConfigureMcpServer)
        .WithStdioServerTransport()
        .WithToolsFromAssembly();

    await builder.Build().RunAsync();
}

// ---------------------------------------------------------------------------
// HTTP host: Streamable HTTP transport over Kestrel. stdout is not a protocol
// channel here, but logging is kept on stderr/stdout console for parity and
// so the same logs work under both hosts.
// ---------------------------------------------------------------------------
static async Task RunHttpAsync(string[] args)
{
    var builder = WebApplication.CreateBuilder(args);

    // Keep logs off stdout corruption concerns and consistent with the stdio host.
    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

    AddMarkdownRagServices(builder.Services, builder.Configuration);

    var transportSettings = builder.Configuration
        .GetSection(MarkdownRagMcfg.MarkdownRagOptions.SectionName)
        .Get<MarkdownRagMcfg.MarkdownRagOptions>()?.TransportSettings
        ?? new MarkdownRagMcfg.TransportOptions();

    // When hosted under IIS/ANCM (in-process or out-of-process) the endpoint is supplied by
    // the host and the address list is made read-only once the pipeline is built, so calling
    // app.Run(url) throws "Changing the URL is not supported because Addresses IsReadOnly".
    // Applying the configured URL on the host builder here instead: self-hosted runs still
    // bind to the configured endpoint, while IIS runs use the ANCM-supplied address and the
    // code never mutates the read-only list.
    var hostedByAncm = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_IIS_PHYSICAL_PATH"));
    if (!hostedByAncm && !string.IsNullOrWhiteSpace(transportSettings.HttpEndpointUrl))
    {
        builder.WebHost.UseUrls(transportSettings.HttpEndpointUrl);
    }

    builder.Services
        .AddMcpServer(ConfigureMcpServer)
        .WithHttpTransport(options =>
        {
            // Stateless is recommended for servers that never issue server-to-client
            // requests (sampling/elicitation); this server only answers tool calls.
            options.Stateless = transportSettings.Stateless;
        })
        .WithToolsFromAssembly();

    var app = builder.Build();

    // Bearer-token authentication gate. When 'server-token' (root-level appsettings key or
    // the ServerToken environment variable) is non-empty, every request must present
    // 'Authorization: Bearer <token>' (or ?access_token= for browser GETs); when it is
    // empty, authentication is disabled entirely. Registered BEFORE the MCP endpoint so
    // /mcp — including initialize — is guarded.
    app.UseMiddleware<BearerTokenMiddleware>();

    // MapMcp exposes the Streamable HTTP endpoint; the optional prefix lets callers
    // mount it under a subpath (e.g. "/mcp") instead of the root.
    app.MapMcp(string.IsNullOrEmpty(transportSettings.HttpRoutePrefix) ? "" : transportSettings.HttpRoutePrefix);

    app.Run();
}

// ---------------------------------------------------------------------------
// Shared DI wiring — identical for both transports.
// ---------------------------------------------------------------------------
static void AddMarkdownRagServices(IServiceCollection services, IConfiguration configuration)
{
    services.AddOptions<MarkdownRagMcfg.MarkdownRagOptions>()
        .Bind(configuration.GetSection(MarkdownRagMcfg.MarkdownRagOptions.SectionName));

    // File-system abstraction (testable; default is the real filesystem).
    services.AddSingleton<IFileSystem, FileSystem>();

    // Index tracking & file discovery.
    services.AddSingleton<IIndexTracker, JsonIndexTracker>();
    services.AddSingleton<IMarkdownFileDiscovery, MarkdownFileDiscovery>();

    // Chunker + tokenizer (shared with the embedding provider so both use the model vocab).
    services.AddSingleton<LazyBertTokenizer>();
    services.AddSingleton<ITextTokenizer>(sp => sp.GetRequiredService<LazyBertTokenizer>());
    services.AddSingleton<IMarkdownChunker, MarkdownChunker>();

    // Embedding provider + first-run model downloader.
    services.AddHttpClient<HuggingFaceModelDownloader>();
    // Registered as both the interface and the concrete type: the preload hosted service needs
    // the concrete type to call EnsureLoadedAsync (a non-interface method).
    services.AddSingleton<OnnxEmbeddingProvider>();
    services.AddSingleton<IEmbeddingProvider>(sp => sp.GetRequiredService<OnnxEmbeddingProvider>());

    // Vector store (local-first in-memory, JSON-persisted).
    services.AddSingleton<IVectorStore, InMemoryVectorStore>();

    // Orchestration service consumed by the MCP tools.
    services.AddSingleton<MarkdownRagService>();

    // Preload the ONNX model in the background at startup so the first tool call doesn't pay
    // the download + session-load cost inside the MCP request path (which can exceed a client's
    // request timeout). Fire-and-forget; tool calls await the load if it's still in progress.
    services.AddHostedService<EmbeddingPreloadHostedService>();
}

static void ConfigureMcpServer(McpServerOptions options)
{
    options.ServerInfo = new() { Name = "mcp-markdown-rag", Version = "0.2.0" };
    options.ServerInstructions = """
        This MCP server provides semantic search capabilities over markdown files using
        vector embeddings and a local vector database. It enables you to index markdown
        documents and perform intelligent searches to find relevant content based on
        semantic similarity rather than just keyword matching.
        """;
}

// Read the Transport setting up front to decide which host to build. The configuration
// sources are the default ones (appsettings.json + environment overrides); a minimal
// configuration builder is enough because the real binding happens inside each host.
static string builder_GetTransport(string[] args)
{
    var config = new ConfigurationBuilder()
        .AddJsonFile("appsettings.json", optional: true)
        .AddEnvironmentVariables()
        .AddCommandLine(args)
        .Build();

    var section = config.GetSection(MarkdownRagMcfg.MarkdownRagOptions.SectionName);
    var transport = section["Transport"];
    return string.IsNullOrWhiteSpace(transport) ? "Stdio" : transport;
}

/// <summary>
/// Makes the auto-generated top-level-statements entry-point class visible to the
/// integration test project (<c>WebApplicationFactory&lt;Program&gt;</c>).
/// </summary>
public partial class Program;

