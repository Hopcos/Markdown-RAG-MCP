using System.IO.Abstractions;
using MarkdownRagMcp.Api;
using MarkdownRagMcp.Abstractions;
using MarkdownRagMcfg = MarkdownRagMcp.Configuration;
using MarkdownRagMcp.Chunking;
using MarkdownRagMcp.Embeddings;
using MarkdownRagMcp.Security;
using MarkdownRagMcp.Services;
using MarkdownRagMcp.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi;
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
// redirected to stderr to avoid corrupting the protocol stream. The environment
// is re-resolved with the ASP.NET precedence (ASPNETCORE_ENVIRONMENT, the
// variable launch profiles like 'hopcos' set, over DOTNET_ENVIRONMENT) because
// Host.CreateApplicationBuilder honors only the DOTNET_ variable.
// ---------------------------------------------------------------------------
static async Task RunStdioAsync(string[] args)
{
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
    {
        EnvironmentName = GetAspNetEnvironment(),
        Args = args
    });

    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

    AddMarkdownRagServices(builder.Services, builder.Configuration);

    builder.Services
        .AddMcpServer(ConfigureMcpServer)
        .WithStdioServerTransport()
        .WithToolsFromAssembly();

    await builder.Build().RunAsync();
}

// ---------------------------------------------------------------------------
// HTTP host: the externally-exposed web surface. The MCP Streamable HTTP
// endpoint shares the pipeline with Swagger UI (OpenAPI document + interactive
// explorer), a read-only REST twin of the MCP discovery surface (/api/tools),
// CORS for browser-hosted frontends, and public discovery/health endpoints.
// stdout is not a protocol channel here, but logging is kept on stderr/stdout
// console for parity and so the same logs work under both hosts.
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

    // The optional bearer token (root-level 'server-token' or its 'ServerToken' fallback):
    // non-empty => guarded by BearerTokenMiddleware; empty => authentication disabled.
    var serverToken = FirstNonEmpty(
        builder.Configuration[MarkdownRagMcfg.MarkdownRagOptions.ServerTokenKey],
        builder.Configuration[MarkdownRagMcfg.MarkdownRagOptions.ServerTokenFallbackKey]);

    // ───────────────────────────────────────────────────────────────
    // CORS so a browser-hosted frontend can integrate with the HTTP surface
    // (preflights are answered without credentials; see BearerTokenMiddleware).
    // ───────────────────────────────────────────────────────────────
    builder.Services.AddCors(options => options.AddPolicy("Frontend", policy => policy
        .AllowAnyOrigin()
        .AllowAnyHeader()
        .AllowAnyMethod()));

    // ───────────────────────────────────────────────────────────────
    // Swagger — enabled unconditionally, including the Release environment.
    // The UI and its JSON document are public (see BearerTokenMiddleware), so
    // the interactive explorer opens without credentials; the MCP endpoint and
    // /api/tools still authenticate via the Authorize dialog when a token is set.
    // ───────────────────────────────────────────────────────────────
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(options =>
    {
        options.SwaggerDoc("v1", new Microsoft.OpenApi.OpenApiInfo
        {
            Title = "Markdown-RAG MCP Server API",
            Version = "v1",
            Description =
                "Read-only REST surface mirroring the MCP discovery surface of the " +
                "Markdown-RAG MCP server (indexing and searching happen through the " +
                "MCP endpoint). " +
                (serverToken is null
                    ? "Authentication is currently disabled ('server-token' not configured)."
                    : "Authenticate with the header 'Authorization: Bearer <server-token>'."),
        });

        options.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.OpenApiSecurityScheme
        {
            Name = "Authorization",
            In = Microsoft.OpenApi.ParameterLocation.Header,
            Type = Microsoft.OpenApi.SecuritySchemeType.ApiKey,
            Description = "Enter: Bearer <server-token>",
        });

        options.AddSecurityRequirement(doc => new Microsoft.OpenApi.OpenApiSecurityRequirement
        {
            [new Microsoft.OpenApi.OpenApiSecuritySchemeReference("Bearer", doc)] = [],
        });
    });

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

    // ───────────────────────────────────────────────────────────────
    // Pipeline: exception mapping → CORS → bearer auth → Swagger → endpoints
    // ───────────────────────────────────────────────────────────────
    app.UseExceptionHandler(errorApp =>
    {
        errorApp.Run(async context =>
        {
            var logger = context.RequestServices
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("MarkdownRagMcp.ErrorHandler");
            var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;

            var (statusCode, errorCode) = error switch
            {
                ArgumentException => (StatusCodes.Status400BadRequest, "BAD_REQUEST"),
                // Malformed requests surfaced by the HTTP plumbing keep their own status.
                BadHttpRequestException bad => (bad.StatusCode, "BAD_REQUEST"),
                TimeoutException => (StatusCodes.Status504GatewayTimeout, "TIMEOUT"),
                _ => (StatusCodes.Status500InternalServerError, "INTERNAL_ERROR"),
            };

            if (statusCode >= 500)
            {
                logger.LogError(error, "Request {Path} failed with {ErrorCode}.", context.Request.Path, errorCode);
            }
            else
            {
                logger.LogWarning("Request {Path} failed with {ErrorCode}: {Message}",
                    context.Request.Path, errorCode, error?.Message);
            }

            context.Response.StatusCode = statusCode;
            await context.Response.WriteAsJsonAsync(new MarkdownRagMcp.Domain.ErrorResponse(
                errorCode, error?.Message ?? "Unexpected server error."));
        });
    });

    app.UseCors("Frontend");

    // Bearer-token authentication gate. When 'server-token' (root-level appsettings key or
    // the ServerToken environment variable) is non-empty, every request must present
    // 'Authorization: Bearer <token>' (or ?access_token= for browser GETs); when it is
    // empty, authentication is disabled entirely. Registered BEFORE the MCP endpoint so
    // /mcp — including initialize — is guarded.
    app.UseMiddleware<BearerTokenMiddleware>();

    // Swagger is served in every environment, including Release.
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "Markdown-RAG MCP Server API");
        options.DocumentTitle = "Markdown-RAG MCP Server";
        options.EnablePersistAuthorization();
    });

    app.MapGet("/", () => Results.Ok(new
    {
        name = "mcp-markdown-rag",
        transport = "MCP Streamable HTTP + stdio",
        mcp = "/" + (string.IsNullOrWhiteSpace(transportSettings.HttpRoutePrefix)
            ? "" : transportSettings.HttpRoutePrefix.TrimStart('/')),
        stateless = transportSettings.Stateless,
        tools = new[] { "index_documents", "search_documents", "clear_index" },
        swagger = "/swagger",
        api = "/api",
        health = "/health",
    })).ExcludeFromDescription();

    app.MapGet("/health", () => Results.Ok(new { status = "ok" })).ExcludeFromDescription();

    // Read-only REST twin of the MCP discovery surface (tools/list).
    app.MapMarkdownRagApiEndpoints();

    // MapMcp exposes the Streamable HTTP endpoint; the optional prefix lets callers
    // mount it under a subpath (e.g. "/mcp") instead of the root.
    app.MapMcp(string.IsNullOrWhiteSpace(transportSettings.HttpRoutePrefix) ? "" : transportSettings.HttpRoutePrefix.TrimStart('/'));

    app.Run();
}

/// <summary>The first non-empty/whitespace value, or null — mirrors the auth contract of the HTTP surface.</summary>
static string? FirstNonEmpty(params string?[] values)
    => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

/// <summary>
/// Resolves the environment with the ASP.NET precedence convention: the
/// <c>ASPNETCORE_ENVIRONMENT</c> variable (what <c>dotnet run</c>'s launch
/// profiles set) wins over the Host-only <c>DOTNET_ENVIRONMENT</c>, defaulting
/// to Production.
/// </summary>
static string GetAspNetEnvironment()
    => FirstNonEmpty(
        Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
        Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"))
        ?? "Production";

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
// sources are the default ones (appsettings.json, the environment-specific
// appsettings.<environment>.json selected by ASPNETCORE_ENVIRONMENT / DOTNET_ENVIRONMENT,
// environment overrides, command line); a minimal configuration builder is enough
// because the real binding happens inside each host.
static string builder_GetTransport(string[] args)
{
    var config = new ConfigurationBuilder()
        .AddJsonFile("appsettings.json", optional: true)
        // The environment-specific appsettings file participates in the transport
        // decision, so a launch profile's environment (e.g. 'hopcos' setting
        // Transport=Http in appsettings.hopcos.json) boots the HTTP surface.
        .AddJsonFile($"appsettings.{GetAspNetEnvironment()}.json", optional: true)
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

