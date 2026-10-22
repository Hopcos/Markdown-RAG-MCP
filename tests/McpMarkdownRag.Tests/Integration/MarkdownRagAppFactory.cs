using MarkdownRagMcp.Abstractions;
using MarkdownRagMcp.Configuration;
using MarkdownRagMcp.Services;
using McpMarkdownRag.Tests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace McpMarkdownRag.Tests.Integration;

/// <summary>
/// Boots the real application pipeline (Program) against TestServer with a deterministic
/// configuration: HTTP transport mounted at <c>/mcp</c>, the index database in a shared
/// temp directory, and the ONNX embedding pipeline replaced by an in-memory fake
/// (no model download, no network needed).
/// </summary>
/// <remarks>
/// The overrides are supplied through environment variables rather than
/// <c>ConfigureWebHost(...ConfigureAppConfiguration)</c> on purpose: Program decides the
/// transport via <c>builder_GetTransport</c> and binds <c>MarkdownRag</c> from
/// <c>WebApplication.CreateBuilder</c>, which executes BEFORE test-injected configuration
/// sources are layered in under the minimal hosting model. Environment variables are
/// already part of the default configuration set the moment the deferred host runs the
/// entry point, so they are the only source that reliably wins at startup-snapshot time.
/// Values are process-wide but identical for every fixture instance; the fixture is
/// effectively a singleton because parallelization is disabled for this assembly.
/// </remarks>
public sealed class MarkdownRagAppFactory : WebApplicationFactory<Program>
{
    /// <summary>Stable per-process temp directory backing DatabasePath (recreated if deleted).</summary>
    public string TempTestDir { get; } = TempDirectory.Ensure();

    public MarkdownRagAppFactory()
    {
        Environment.SetEnvironmentVariable("MarkdownRag__Transport", "Http");
        Environment.SetEnvironmentVariable("MarkdownRag__TransportSettings__HttpEndpointUrl", "http://127.0.0.1:3001");
        // Mount the MCP endpoint under a subpath (matches how production deployments look).
        Environment.SetEnvironmentVariable("MarkdownRag__TransportSettings__HttpRoutePrefix", "mcp");
        Environment.SetEnvironmentVariable("MarkdownRag__TransportSettings__Stateless", "true");
        Environment.SetEnvironmentVariable("MarkdownRag__DatabasePath", TempTestDir);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Replace the real embedding pipeline with a deterministic fake AFTER Program's own
        // registrations ran, so every tool call resolves against synthetic vectors.
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IEmbeddingProvider>(new FakeEmbeddingProvider(dimension: 16));
            services.AddSingleton<ITextTokenizer>(new SimpleTestTokenizer());

            // Remove the background preload hosted service: it requires the concrete
            // OnnxEmbeddingProvider (model download / session load) which the fake replaces.
            foreach (var d in services
                .Where(d => d.ServiceType == typeof(IHostedService)
                            && d.ImplementationType == typeof(EmbeddingPreloadHostedService))
                .ToList())
            {
                services.Remove(d);
            }
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            TempDirectory.Remove();
        }
    }
}

/// <summary>
/// Lazily-created, best-effort-deleted shared temp directory used by the integration fixture
/// and for staging sample markdown documents. Recreated on demand between fixture runs.
/// </summary>
public static class TempDirectory
{
    private static string? _path;

    public static string Ensure()
    {
        if (_path is null)
        {
            _path = Path.Combine(Path.GetTempPath(), "mcp_rag_factory_" + Guid.NewGuid().ToString("N"));
        }
        Directory.CreateDirectory(_path);
        return _path;
    }

    public static void Remove()
    {
        if (_path is not null && Directory.Exists(_path))
        {
            try { Directory.Delete(_path, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
        _path = null;
    }
}
