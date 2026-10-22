using MarkdownRagMcp.Embeddings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MarkdownRagMcp.Services;

/// <summary>
/// Starts a background model preload as soon as the host is running, so the first tool call
/// does not pay the download + ONNX-session load cost inside the MCP request path (which can
/// exceed a client's request timeout). The preload is fire-and-forget: tool calls that arrive
/// before it finishes simply await <c>EnsureLoadedAsync</c> on the provider.
/// </summary>
public sealed class EmbeddingPreloadHostedService : IHostedService
{
    private readonly OnnxEmbeddingProvider _provider;
    private readonly ILogger<EmbeddingPreloadHostedService> _logger;
    private Task? _preloadTask;
    private readonly CancellationTokenSource _cts = new();

    public EmbeddingPreloadHostedService(
        OnnxEmbeddingProvider provider,
        ILogger<EmbeddingPreloadHostedService> logger)
    {
        _provider = provider;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Preloading embedding model in the background...");
        _preloadTask = Task.Run(async () =>
        {
            try
            {
                await _provider.EnsureLoadedAsync(_cts.Token);
                _logger.LogInformation("Embedding model preload complete.");
            }
            catch (OperationCanceledException)
            {
                // Host shutting down before preload finished — fine.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Embedding model preload failed; it will be retried on the first tool call.");
            }
        }, _cts.Token);

        // Returning immediately means the host (and the MCP handshake) are not delayed by the
        // preload. The task continues in the background.
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts.Cancel();
        if (_preloadTask is not null)
        {
            try { await _preloadTask.WaitAsync(cancellationToken); }
            catch { /* swallow; best-effort */ }
        }
        _cts.Dispose();
    }
}
