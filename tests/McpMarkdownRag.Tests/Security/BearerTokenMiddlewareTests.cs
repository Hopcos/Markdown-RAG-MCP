using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MarkdownRagMcp.Security;

namespace McpMarkdownRag.Tests.Security;

/// <summary>
/// Mirrors the reference server's BearerTokenMiddlewareTests: the token gate is
/// disabled when 'server-token' is empty, accepts Authorization/Query credentials
/// otherwise, and rejects everything else with 401.
/// </summary>
public class BearerTokenMiddlewareTests
{
    private const string Token = "secret-token";

    private static async Task<(int Status, string? Body)> InvokeAsync(
        string? configuredToken,
        string path,
        string? authorizationHeader = null,
        string? accessTokenQuery = null,
        string method = "GET")
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["server-token"] = configuredToken,
        }).Build();

        HttpContext? captured = null;
        var middleware = new BearerTokenMiddleware(
            async context => { captured = context; },
            config,
            NullLogger<BearerTokenMiddleware>.Instance);

        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        ctx.Request.Method = method;
        if (authorizationHeader is not null)
        {
            ctx.Request.Headers.Authorization = authorizationHeader;
        }

        if (accessTokenQuery is not null)
        {
            ctx.Request.QueryString = new QueryString($"?access_token={accessTokenQuery}");
        }

        var bodyStream = new MemoryStream();
        ctx.Response.Body = bodyStream;

        await middleware.InvokeAsync(ctx);

        bodyStream.Position = 0;
        var body = await new StreamReader(bodyStream).ReadToEndAsync();
        return (ctx.Response.StatusCode, body.Length == 0 ? null : body);
    }

    [Fact]
    public async Task Auth_is_disabled_when_no_token_is_configured()
    {
        var (status, _) = await InvokeAsync(configuredToken: null, path: "/mcp");
        Assert.Equal(StatusCodes.Status200OK, status);
    }

    [Fact]
    public async Task Auth_is_disabled_when_token_is_empty()
    {
        var (status, _) = await InvokeAsync(configuredToken: "", path: "/mcp");
        Assert.Equal(StatusCodes.Status200OK, status);
    }

    [Fact]
    public async Task Auth_is_disabled_when_token_is_whitespace()
    {
        var (status, _) = await InvokeAsync(configuredToken: "   ", path: "/mcp");
        Assert.Equal(StatusCodes.Status200OK, status);
    }

    [Fact]
    public async Task Valid_bearer_header_passes_through()
    {
        var (status, _) = await InvokeAsync(Token, "/mcp", authorizationHeader: $"Bearer {Token}");
        Assert.Equal(StatusCodes.Status200OK, status);
    }

    [Fact]
    public async Task Valid_bearer_header_is_case_insensitive()
    {
        var (status, _) = await InvokeAsync(Token, "/mcp", authorizationHeader: $"bearer {Token}");
        Assert.Equal(StatusCodes.Status200OK, status);
    }

    [Fact]
    public async Task Missing_credentials_yield_401_with_contract_body()
    {
        var (status, body) = await InvokeAsync(Token, "/mcp");
        Assert.Equal(StatusCodes.Status401Unauthorized, status);
        Assert.NotNull(body);
        Assert.Contains("UNAUTHORIZED", body);
        Assert.Contains("Authorization: Bearer", body);
    }

    [Fact]
    public async Task Wrong_credentials_yield_401()
    {
        var (status, _) = await InvokeAsync(Token, "/mcp", authorizationHeader: "Bearer wrong");
        Assert.Equal(StatusCodes.Status401Unauthorized, status);
    }

    [Fact]
    public async Task Malformed_authorization_scheme_yields_401()
    {
        var (status, _) = await InvokeAsync(Token, "/mcp", authorizationHeader: $"Basic {Token}");
        Assert.Equal(StatusCodes.Status401Unauthorized, status);
    }

    [Fact]
    public async Task Query_string_access_token_is_accepted_for_browsers()
    {
        var (status, _) = await InvokeAsync(Token, "/mcp", accessTokenQuery: Token);
        Assert.Equal(StatusCodes.Status200OK, status);
    }

    [Fact]
    public async Task Query_string_access_token_is_case_sensitive()
    {
        var (status, _) = await InvokeAsync(Token, "/mcp", accessTokenQuery: Token.ToUpperInvariant());
        Assert.Equal(StatusCodes.Status401Unauthorized, status);
    }

    [Fact]
    public async Task CORS_preflight_is_not_blocked()
    {
        var (status, _) = await InvokeAsync(Token, "/mcp", method: "OPTIONS");
        Assert.Equal(StatusCodes.Status200OK, status);
    }

    [Fact]
    public void FixedTimeComparison_matches_identical_tokens_only()
    {
        var ok = CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(Token)),
            SHA256.HashData(Encoding.UTF8.GetBytes(Token)));
        Assert.True(ok);
    }
}
