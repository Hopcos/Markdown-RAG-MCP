using MarkdownRagMcp.Abstractions;
using MarkdownRagMcp.Services;
using McpMarkdownRag.Tests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace McpMarkdownRag.Tests.Integration;

/// <summary>
/// Boots the real application pipeline (Program) with bearer-token authentication
/// ENABLED: the token is supplied through the <c>ServerToken</c> environment variable
/// (the Windows-legal fallback key of the root-level <c>server-token</c> configuration),
/// so the middleware registered in Program guards the /mcp endpoint. Used to verify the
/// 401-without-credentials / 200-with-valid-bearer contract end-to-end.
/// </summary>
/// <remarks>
/// The environment variables set by <see cref="MarkdownRagAppFactory"/> are process-wide and
/// identical for every fixture (transport envs, temp database path), so this factory only
/// adds the token. It must be cleared on dispose so fixtures created later (e.g. the main
/// integration class's factory) run with authentication disabled. Parallelization is
/// disabled for this assembly, so class lifetimes never overlap.
/// </remarks>
public sealed class AuthenticatedAppFactory : WebApplicationFactory<Program>
{
    private const string TestToken = "test-server-token";

    /// <summary>The bearer token this fixture's server requires.</summary>
    public const string Token = TestToken;

    public AuthenticatedAppFactory()
    {
        // Mirror the main factory's transport configuration (also correct when this class
        // runs first and the transport env vars are not set yet).
        Environment.SetEnvironmentVariable("MarkdownRag__Transport", "Http");
        Environment.SetEnvironmentVariable("MarkdownRag__TransportSettings__HttpEndpointUrl", "http://127.0.0.1:3001");
        Environment.SetEnvironmentVariable("MarkdownRag__TransportSettings__HttpRoutePrefix", "mcp");
        Environment.SetEnvironmentVariable("MarkdownRag__TransportSettings__Stateless", "true");
        Environment.SetEnvironmentVariable("MarkdownRag__DatabasePath", TempDirectory.Ensure());

        // Enable the token gate ("ServerToken" is the platform-convenient fallback key of
        // the root-level "server-token" configuration).
        Environment.SetEnvironmentVariable("ServerToken", TestToken);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Same deterministic fake pipeline as the main factory: no model download, no network.
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IEmbeddingProvider>(new FakeEmbeddingProvider(dimension: 16));
            services.AddSingleton<ITextTokenizer>(new SimpleTestTokenizer());

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
            Environment.SetEnvironmentVariable("ServerToken", null);
            TempDirectory.Remove();
        }
    }
}
