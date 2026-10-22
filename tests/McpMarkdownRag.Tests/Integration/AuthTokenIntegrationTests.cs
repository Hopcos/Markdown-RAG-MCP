using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace McpMarkdownRag.Tests.Integration;

/// <summary>
/// Verifies the bearer-token gate end-to-end on the real /mcp endpoint: the server
/// fixture (<see cref="AuthenticatedAppFactory"/>) runs with a configured token, so
/// credentials-less MCP requests are rejected with the auth contract's 401 payload and
/// valid bearer credentials initialize the MCP session normally.
/// </summary>
public class AuthTokenIntegrationTests : IClassFixture<AuthenticatedAppFactory>
{
    private const string Initialize = """
        {"jsonrpc":"2.0","id":1,"method":"initialize","params":{
          "protocolVersion":"2025-06-18","capabilities":{},
          "clientInfo":{"name":"integration-test","version":"1.0"}}}
        """;

    private readonly AuthenticatedAppFactory _factory;

    public AuthTokenIntegrationTests(AuthenticatedAppFactory factory)
    {
        _factory = factory;
    }

    private HttpClient CreateMcpClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return client;
    }

    private static StringContent JsonContent(string payload) =>
        new(payload, Encoding.UTF8, "application/json");

    /// <summary>Posts a JSON-RPC payload and returns status + body WITHOUT EnsureSuccessStatusCode
    /// (the auth gate answers 401, which is itself part of the contract).</summary>
    private static async Task<(HttpStatusCode Status, string Body)> PostJsonRpc(HttpClient client, string payload, string? bearerToken = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp");
        request.Content = JsonContent(payload);
        if (bearerToken is not null)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);
        }

        using var response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static bool IsSuccess(HttpStatusCode status) => (int)status >= 200 && (int)status < 300;

    [Fact]
    public async Task Credentials_less_requests_are_rejected_with_401()
    {
        using var client = CreateMcpClient();
        var (status, body) = await PostJsonRpc(client, Initialize);

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Contains("UNAUTHORIZED", body);
        Assert.Contains("Authorization: Bearer", body);
    }

    [Fact]
    public async Task Wrong_bearer_token_is_rejected_with_401()
    {
        using var client = CreateMcpClient();
        var (status, body) = await PostJsonRpc(client, Initialize, bearerToken: "wrong-token");

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Contains("UNAUTHORIZED", body);
    }

    [Fact]
    public async Task Valid_bearer_token_initializes_the_mcp_session()
    {
        using var client = CreateMcpClient();
        var (status, body) = await PostJsonRpc(client, Initialize, bearerToken: AuthenticatedAppFactory.Token);

        Assert.True(IsSuccess(status), $"{(int)status} {body}");
        Assert.Contains("\"serverInfo\"", body);
        Assert.Contains("mcp-markdown-rag", body);
    }

    [Fact]
    public async Task Valid_bearer_token_reaches_tools_list()
    {
        using var client = CreateMcpClient();
        var (status, body) = await PostJsonRpc(
            client, """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""", bearerToken: AuthenticatedAppFactory.Token);

        Assert.True(IsSuccess(status), $"{(int)status} {body}");
        Assert.Contains("\"name\":\"index_documents\"", body);
    }

    /// <summary>The REST discovery twin is NOT in the public-path exemption list: it
    /// answers 401 without credentials, exactly like the MCP endpoint.</summary>
    [Fact]
    public async Task Tool_catalog_requires_authentication_when_token_configured()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/tools");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("UNAUTHORIZED", await response.Content.ReadAsStringAsync());
    }

    /// <summary>The public-path exemptions work end-to-end: with the token gate
    /// ENABLED the Swagger UI/document, root discovery and health check stay
    /// reachable without any credentials.</summary>
    [Fact]
    public async Task Public_paths_stay_reachable_without_credentials()
    {
        using var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/swagger/v1/swagger.json")).StatusCode);
    }
}
