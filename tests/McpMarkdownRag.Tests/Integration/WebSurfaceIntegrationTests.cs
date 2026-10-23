using System.IO;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using MarkdownRagMcp.Api;

namespace McpMarkdownRag.Tests.Integration;

/// <summary>
/// Exercises the externally-exposed HTTP web surface end-to-end on the real
/// pipeline: the public discovery endpoints (/ and /health), the public Swagger
/// document, and the read-only REST twin of the MCP discovery surface
/// (/api/tools — a live-resolved mirror of tools/list). The fixture runs with
/// authentication disabled, so all paths are reachable without credentials.
/// </summary>
public class WebSurfaceIntegrationTests : IClassFixture<MarkdownRagAppFactory>
{
    private readonly MarkdownRagAppFactory _factory;

    public WebSurfaceIntegrationTests(MarkdownRagAppFactory factory)
    {
        _factory = factory;
    }

    private HttpClient CreateClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    [Fact]
    public async Task Root_endpoint_is_public_and_returns_server_info()
    {
        using var client = CreateClient();
        var response = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("mcp-markdown-rag", content);
        Assert.Contains("/mcp", content);
        Assert.Contains("/swagger", content);
        Assert.Contains("/api", content);
        Assert.Contains("/health", content);
    }

    [Fact]
    public async Task Health_endpoint_is_public_and_healthy()
    {
        using var client = CreateClient();
        var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("ok", content);
    }

    [Fact]
    public async Task Swagger_document_is_public_and_describes_routes()
    {
        using var client = CreateClient();
        var response = await client.GetAsync("/swagger/v1/swagger.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("Markdown-RAG MCP Server API", content);
        Assert.Contains("/api/tools", content);
    }

    [Fact]
    public async Task Tool_catalog_returns_the_three_registered_mcp_tools()
    {
        using var client = CreateClient();
        var result = await client.GetFromJsonAsync<ToolCatalogResult>("/api/tools");

        Assert.NotNull(result);
        Assert.Equal(3, result.Count);
        Assert.Contains(result.Tools, tool => tool.Name == "index_documents");
        Assert.Contains(result.Tools, tool => tool.Name == "search_documents");
        Assert.Contains(result.Tools, tool => tool.Name == "clear_index");
    }

    [Fact]
    public async Task Tool_catalog_reports_typed_parameter_rows()
    {
        using var client = CreateClient();
        var result = await client.GetFromJsonAsync<ToolCatalogResult>("/api/tools");
        Assert.NotNull(result);
        var search = Assert.Single(result.Tools, tool => tool.Name == "search_documents");

        Assert.Equal(2, search.Parameters.Count);
        var query = Assert.Single(search.Parameters, parameter => parameter.Name == "query");
        Assert.True(query.Required);
        Assert.Equal("string", query.Type);
        Assert.Contains("Query to search for", query.Description);

        var k = Assert.Single(search.Parameters, parameter => parameter.Name == "k");
        Assert.False(k.Required);
        Assert.Equal("integer", k.Type);
        Assert.Contains("Number of documents to return", k.Description);
    }

    /// <summary>
    /// Proves the transport decision is environment-aware: with ASPNETCORE_ENVIRONMENT=hopcos
    /// (what 'dotnet run -lp hopcos' sets from the launch profile) the decision reads
    /// appsettings.hopcos.json (Transport=Http) and boots the externally-exposed HTTP web
    /// surface. No Transport override is configured, so only the environment-specific
    /// appsettings file can decide — without it the stdio host would boot and the
    /// TestServer-backed client would fail.
    /// </summary>
    [Fact]
    public async Task Transport_decision_is_environment_aware_and_boots_http_surface()
    {
        // The transport decision resolves the appsettings files CWD-relative, matching
        // how 'dotnet run' starts the server inside the server project directory.
        var serverProjectDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            Path.Join("..", "..", "..", "..", "..", "src", "McpMarkdownRag")));
        var originalCwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(serverProjectDir);
        try
        {
            using var factory = new HopcosEnvironmentAppFactory();
            using var client = factory.CreateClient();

            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
            using var rootResponse = await client.GetAsync("/");
            Assert.Equal(HttpStatusCode.OK, rootResponse.StatusCode);
        }
        finally
        {
            Directory.SetCurrentDirectory(originalCwd);
        }
    }
}
