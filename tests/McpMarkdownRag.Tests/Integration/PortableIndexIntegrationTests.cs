using System.Net.Http.Headers;
using System.Text;
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
/// Proves the index is fully portable: after a local indexing run, ONLY the persisted
/// vectors.json is needed to answer semantic searches on another server. The chunk text
/// and file name travel inside vectors.json, so search_documents never reads the original
/// markdown files and does not need index_tracking.json either — exactly the
/// "index here, query there" deployment.
/// </summary>
public class PortableIndexIntegrationTests
{
    [Fact]
    public async Task Search_works_from_vectors_json_only_without_source_markdown()
    {
        var root = Path.Combine(Path.GetTempPath(), "portable_index_" + Guid.NewGuid().ToString("N"));
        var indexDir = Path.Combine(root, "index");
        var remoteDir = Path.Combine(root, "remote");
        Directory.CreateDirectory(Path.Combine(indexDir, "notes"));
        Directory.CreateDirectory(remoteDir);

        try
        {
            // ── Phase 1: index a source markdown file locally (the source stays local). ──
            var sourceDoc = Path.Combine(indexDir, "notes", "portable.md");
            await File.WriteAllTextAsync(sourceDoc,
                "# Portable Index\nAlpha-beta-gamma signals trigger when the baseline threshold is exceeded.");

            using (var indexFactory = new PortableIndexAppFactory(indexDir))
            {
                using var client = CreateMcpClient(indexFactory);
                var indexRpc = """
                    {"jsonrpc":"2.0","id":10,"method":"tools/call","params":{
                      "name":"index_documents",
                      "arguments":{
                        "current_working_directory":"__CWD__",
                        "directory":"notes",
                        "recursive":false,
                        "force_reindex":true}}}
                    """.Replace("__CWD__", indexDir.Replace("\\", "/"));

                var indexResponse = await PostJsonRpc(client, indexRpc);
                Assert.Contains("Full reindex", indexResponse);
            }

            // ── Phase 2: ship ONLY vectors.json to the remote server. ──
            var vectorsJson = Path.Combine(indexDir, "vectors.json");
            Assert.True(File.Exists(vectorsJson), "indexing must produce vectors.json");
            File.Copy(vectorsJson, Path.Combine(remoteDir, "vectors.json"));

            // The remote box has no markdown files and no incremental-tracking state.
            Assert.False(Directory.EnumerateFiles(remoteDir, "*.md", SearchOption.AllDirectories).Any(),
                "remote server must not hold source markdown files");
            Assert.False(File.Exists(Path.Combine(remoteDir, "index_tracking.json")),
                "remote server must not need index_tracking.json");

            // ── Phase 3: a brand-new server over the copied index answers searches. ──
            using (var remoteFactory = new PortableIndexAppFactory(remoteDir))
            {
                using var client = CreateMcpClient(remoteFactory);
                var searchRpc = """
                    {"jsonrpc":"2.0","id":11,"method":"tools/call","params":{
                      "name":"search_documents",
                      "arguments":{"query":"baseline threshold","k":5}}}
                    """;

                var searchResponse = await PostJsonRpc(client, searchRpc);
                // The file name and the FULL chunk text come out of vectors.json alone.
                Assert.Contains("portable.md", searchResponse);
                Assert.Contains("Alpha-beta-gamma signals trigger", searchResponse);
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }

    private static StringContent JsonContent(string payload) =>
        new(payload, Encoding.UTF8, "application/json");

    private HttpClient CreateMcpClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return client;
    }

    private static async Task<string> PostJsonRpc(HttpClient client, string payload)
    {
        using var response = await client.PostAsync("/mcp", JsonContent(payload));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// Test host mirroring MarkdownRagAppFactory's wiring (fake embeddings, no model
    /// download) but with a caller-supplied database directory, so the same test code can
    /// boot the indexing server and, later, an isolated query-only server over the copy.
    /// </summary>
    private sealed class PortableIndexAppFactory : WebApplicationFactory<Program>
    {
        public PortableIndexAppFactory(string databasePath)
        {
            Environment.SetEnvironmentVariable("MarkdownRag__Transport", "Http");
            Environment.SetEnvironmentVariable("MarkdownRag__TransportSettings__HttpEndpointUrl", "http://127.0.0.1:3001");
            Environment.SetEnvironmentVariable("MarkdownRag__TransportSettings__HttpRoutePrefix", "mcp");
            Environment.SetEnvironmentVariable("MarkdownRag__TransportSettings__Stateless", "true");
            Environment.SetEnvironmentVariable("MarkdownRag__DatabasePath", databasePath);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
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
    }
}
