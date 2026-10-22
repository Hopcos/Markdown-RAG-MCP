using System.ComponentModel;
using System.Reflection;
using MarkdownRagMcp.Services;
using ModelContextProtocol.Server;

namespace MarkdownRagMcp.Api;

/// <summary>
/// Read-only REST surface for frontend integration — a REST twin of the MCP
/// discovery surface (<c>tools/list</c>). The MCP surface itself is exclusively
/// served at the configured MCP endpoint; tool calls always go through it, so
/// no index/search/clear operations exist on this surface.
/// </summary>
/// <remarks>
/// The catalog is resolved live on every request from the <c>[McpServerToolType]</c>
/// / <c>[McpServerTool]</c> annotations of the entry assembly — the same discovery
/// rule the MCP SDK applies — so /api/tools can never drift from what
/// <c>tools/list</c> reports. Parameters whose types resolve from DI (e.g.
/// <see cref="MarkdownRagService"/>) or belong to the protocol plumbing
/// (RequestContext, CancellationToken, service providers) are excluded, mirroring
/// how the SDK's input-schema generation treats them as injected dependencies.
/// </remarks>
public static class MarkdownRagApiEndpoints
{
    public static IEndpointRouteBuilder MapMarkdownRagApiEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api").WithTags("Markdown-RAG MCP Server API");

        group.MapGet("/tools", ListToolsAsync)
            .WithName("ListTools")
            .WithSummary("REST twin of the tools/list MCP surface.")
            .WithDescription("Returns every registered MCP tool: name, description and typed parameter rows.")
            .Produces<ToolCatalogResult>();

        return endpoints;
    }

    private static Task<IResult> ListToolsAsync(IServiceProvider services)
    {
        var catalog = new List<MarkdownToolStatus>();
        foreach (var type in typeof(Program).Assembly.GetTypes())
        {
            if (type.GetCustomAttribute<McpServerToolTypeAttribute>() is null)
            {
                continue;
            }

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                var tool = method.GetCustomAttribute<McpServerToolAttribute>();
                if (tool is null)
                {
                    continue;
                }

                catalog.Add(new MarkdownToolStatus(
                    tool.Name ?? DefaultToolName(method),
                    method.GetCustomAttribute<DescriptionAttribute>()?.Description,
                    BuildParameterRows(method, services).AsReadOnly()));
            }
        }

        return Task.FromResult<IResult>(Results.Ok(new ToolCatalogResult(catalog.Count, catalog.AsReadOnly())));
    }

    /// <summary>SDK-convention fallback: the method name minus an "Async" suffix, in lower snake-case.</summary>
    private static string DefaultToolName(MethodInfo method)
    {
        var name = method.Name;
        if (name.EndsWith("Async", StringComparison.Ordinal))
        {
            name = name[..^"Async".Length];
        }

        var snake = new System.Text.StringBuilder();
        foreach (var ch in name)
        {
            if (char.IsUpper(ch))
            {
                if (snake.Length > 0)
                {
                    snake.Append('_');
                }
                snake.Append(char.ToLowerInvariant(ch));
            }
            else
            {
                snake.Append(char.ToLowerInvariant(ch));
            }
        }

        return snake.ToString();
    }

    /// <summary>
    /// Typed parameter rows of one tool, in declaration order: protocol plumbing
    /// (RequestContext, CancellationToken, service providers) and DI-resolvable
    /// parameters are excluded — the same rule the SDK applies when it builds the
    /// MCP input schema — so the rows match what tools/list reports.
    /// </summary>
    private static List<ToolParameterStatus> BuildParameterRows(MethodInfo method, IServiceProvider services)
    {
        var rows = new List<ToolParameterStatus>();
        foreach (var parameter in method.GetParameters())
        {
            if (IsFrameworkParameter(parameter.ParameterType) ||
                services.GetService(parameter.ParameterType) is not null)
            {
                continue;
            }

            rows.Add(new ToolParameterStatus(
                parameter.Name!,
                JsonTypeName(parameter.ParameterType),
                parameter.GetCustomAttribute<DescriptionAttribute>()?.Description,
                IsRequiredParameter(parameter)));
        }

        return rows;
    }

    /// <summary>Protocol plumbing never shows up in an input schema.</summary>
    private static bool IsFrameworkParameter(Type parameterType) =>
        parameterType == typeof(CancellationToken) ||
        parameterType == typeof(IServiceProvider) ||
        // Open-generic name check (RequestContext`1's definition): the protocol's
        // per-request RequestContext<T> is never a schema parameter.
        (parameterType.IsGenericType &&
         parameterType.GetGenericTypeDefinition().Name == "RequestContext");

    private static string JsonTypeName(Type parameterType) => parameterType switch
    {
        _ when parameterType == typeof(string) => "string",
        _ when parameterType == typeof(int) => "integer",
        _ when parameterType == typeof(bool) => "boolean",
        _ when parameterType == typeof(double) || parameterType == typeof(float) => "number",
        _ => "object",
    };

    /// <summary>A parameter without a default value is a required input schema property.</summary>
    private static bool IsRequiredParameter(ParameterInfo parameter) =>
        (parameter.Attributes & ParameterAttributes.HasDefault) == 0;
}

/// <summary>One row of the tools/list discovery surface and its REST twin.</summary>
public sealed record MarkdownToolStatus(
    string Name,
    string? Description,
    IReadOnlyList<ToolParameterStatus> Parameters);

/// <summary>One typed parameter of an MCP tool.</summary>
public sealed record ToolParameterStatus(string Name, string Type, string? Description, bool Required);

/// <summary>Root payload of the GET /api/tools REST twin.</summary>
public sealed record ToolCatalogResult(int Count, IReadOnlyList<MarkdownToolStatus> Tools);
