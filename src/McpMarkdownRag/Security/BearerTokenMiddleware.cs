using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MarkdownRagMcp.Security;

/// <summary>
/// Enforces optional HTTP bearer-token authentication for the whole server.
/// <para>
/// When the <c>server-token</c> configuration value (appsettings.json root level, or the
/// environment variable <c>server-token</c> / <c>ServerToken</c>) is non-empty, every request
/// must present a matching credential: <c>Authorization: Bearer &lt;token&gt;</c> — except
/// CORS preflight (OPTIONS) requests, which are sent by browsers without custom headers. GET
/// requests may alternatively carry <c>?access_token=&lt;token&gt;</c>. A missing or incorrect
/// credential yields 401 with <c>WWW-Authenticate: Bearer</c> and a JSON error payload.
/// </para>
/// <para>
/// When the value is empty or missing, authentication is disabled entirely. The stdio
/// transport never goes through the HTTP pipeline, so it is unaffected (a stdio host is the
/// trusted child process of the MCP client that registered the server).
/// </para>
/// </summary>
public sealed class BearerTokenMiddleware
{
    private const string BearerPrefix = "Bearer ";

    private readonly RequestDelegate _next;
    private readonly ILogger<BearerTokenMiddleware> _logger;
    private readonly string? _token;

    public BearerTokenMiddleware(
        RequestDelegate next,
        IConfiguration configuration,
        ILogger<BearerTokenMiddleware> logger)
    {
        _next = next;
        _logger = logger;

        _token = FirstNonEmpty(
            configuration[Configuration.MarkdownRagOptions.ServerTokenKey],
            configuration[Configuration.MarkdownRagOptions.ServerTokenFallbackKey]);

        if (_token is null)
        {
            logger.LogInformation("Bearer token authentication is disabled ('server-token' is not configured).");
        }
        else
        {
            logger.LogInformation("Bearer token authentication is enabled for all endpoints.");
        }
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (_token is null)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // CORS preflight requests are sent by browsers without custom headers;
        // they are answered by the CORS middleware and never carry credentials.
        if (HttpMethods.IsOptions(context.Request.Method))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (IsAuthorized(context.Request))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        _logger.LogWarning(
            "Rejected {Method} {Path}: missing or invalid bearer token.",
            context.Request.Method,
            context.Request.Path);

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Bearer";
        await context.Response.WriteAsJsonAsync(new
        {
            error = "UNAUTHORIZED",
            message = "Invalid authorization: this server requires the header 'Authorization: Bearer <server-token>'."
        }).ConfigureAwait(false);
    }

    private bool IsAuthorized(HttpRequest request)
    {
        var authorization = request.Headers.Authorization.ToString();
        if (authorization.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var provided = authorization[BearerPrefix.Length..].Trim();
            return FixedTimeEquals(provided, _token!);
        }

        // Browser convenience fallback: same token, delivered as a query string value
        // (useful for GET requests issued from a browser address bar).
        if (request.Query.TryGetValue("access_token", out var queryToken) &&
            FixedTimeEquals(queryToken.ToString(), _token!))
        {
            return true;
        }

        return false;
    }

    /// <summary>Constant-time comparison to avoid leaking the token length/content via timing.</summary>
    private static bool FixedTimeEquals(string provided, string expected)
    {
        var providedHash = SHA256.HashData(Encoding.UTF8.GetBytes(provided));
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(providedHash, expectedHash);
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
