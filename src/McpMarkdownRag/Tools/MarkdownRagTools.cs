using System.ComponentModel;
using MarkdownRagMcp.Services;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace MarkdownRagMcp.Tools;

/// <summary>
/// MCP tools exposed by the server. Each method mirrors a tool from the original Python
/// implementation: <c>index_documents</c>, <c>search_documents</c> and <c>clear_index</c>.
/// Tool methods stay thin and delegate to <see cref="MarkdownRagService"/>; the service is
/// resolved from DI by the MCP SDK, so this layer is purely a protocol adapter.
/// </summary>
/// <remarks>
/// Long-running indexing of large corpora can exceed a client's request timeout (e.g. the
/// MCP inspector's ~60 s). To keep such calls alive, <c>index_documents</c> reports progress
/// via <c>notifications/progress</c> — clients that honor progress notifications reset their
/// idle timeout on each update. The progress token is read from the request and forwarded to
/// the service as a simple callback.
/// </remarks>
[McpServerToolType]
public static class MarkdownRagTools
{
    [McpServerTool(Name = "index_documents"),
     Description("Index Markdown files for semantic search. Converts each file into structured vector chunks and stores them in the local vector database. Performs an incremental update by default (only changed files); pass force_reindex=true to rebuild the whole index. Large corpora are embedded in batches with progress notifications.")]
    public static async Task<IndexResult> IndexDocumentsAsync(
        MarkdownRagService service,
        RequestContext<CallToolRequestParams> context,
        [Description("Current working directory")] string current_working_directory,
        CancellationToken cancellationToken,
        [Description("Directory to index (relative to current working directory)")] string directory = "",
        [Description("Recursively index subdirectories")] bool recursive = false,
        [Description("Force a full reindex of all files")] bool force_reindex = false)
    {
        var progress = CreateProgressReporter(context);
        return await service.IndexDocumentsAsync(
            current_working_directory, directory, recursive, force_reindex,
            progress, cancellationToken);
    }

    [McpServerTool(Name = "search_documents"),
     Description("Search for semantically relevant documents based on a natural-language query.")]
    public static async Task<string> SearchDocumentsAsync(
        MarkdownRagService service,
        [Description("Query to search for")] string query,
        CancellationToken cancellationToken,
        [Description("Number of documents to return")] int k = 5)
    {
        return await service.SearchDocumentsAsync(query, k, cancellationToken);
    }

    [McpServerTool(Name = "clear_index"),
     Description("Clear the vector database's collection and reset the tracking file.")]
    public static async Task<string> ClearIndexAsync(
        MarkdownRagService service,
        CancellationToken cancellationToken)
    {
        await service.ClearIndexAsync(cancellationToken);
        return "Index cleared and tracking file reset.";
    }

    /// <summary>
    /// Build a progress callback that emits <c>notifications/progress</c> when the client
    /// supplied a progress token. Returns a no-op when no token was provided (clients are
    /// not required to support progress).
    /// </summary>
    private static Func<double, double?, string?, CancellationToken, Task> CreateProgressReporter(
        RequestContext<CallToolRequestParams> context)
    {
        var token = context.Params?.ProgressToken;
        if (token is null)
        {
            return (_, _, _, _) => Task.CompletedTask;
        }

        var progressToken = token.Value;
        var server = context.Server;
        return async (progress, total, message, ct) =>
        {
            var p = new ProgressNotificationParams
            {
                ProgressToken = progressToken,
                Progress = new ProgressNotificationValue
                {
                    Progress = (float)progress,
                    Total = total is null ? null : (float)total,
                    Message = message,
                },
            };
            await server.SendNotificationAsync(NotificationMethods.ProgressNotification, p, serializerOptions: null, cancellationToken: ct);
        };
    }
}
