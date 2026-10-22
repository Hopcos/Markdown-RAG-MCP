using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;

namespace McpMarkdownRag.Tests.Integration;

/// <summary>
/// Exercises the MCP Streamable HTTP contract on the real /mcp endpoint:
/// handshake, tool discovery and the full index → search → clear tool-call flow.
/// The fixture swaps the ONNX pipeline for a deterministic fake, so no model
/// download or network access happens.
/// </summary>
public class McpIntegrationTests : IClassFixture<MarkdownRagAppFactory>
{
    private readonly MarkdownRagAppFactory _factory;

    public McpIntegrationTests(MarkdownRagAppFactory factory)
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

    private static async Task<string> PostJsonRpc(HttpClient client, string payload)
    {
        using var response = await client.PostAsync("/mcp", JsonContent(payload));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private const string Initialize = """
        {"jsonrpc":"2.0","id":1,"method":"initialize","params":{
          "protocolVersion":"2025-06-18","capabilities":{},
          "clientInfo":{"name":"integration-test","version":"1.0"}}}
        """;

    [Fact]
    public async Task Initialize_returns_server_info_and_instructions()
    {
        using var client = CreateMcpClient();
        var body = await PostJsonRpc(client, Initialize);

        Assert.Contains("\"serverInfo\"", body);
        Assert.Contains("mcp-markdown-rag", body);
        Assert.Contains("instructions", body);
    }

    [Fact]
    public async Task Tools_list_exposes_three_rag_tools()
    {
        using var client = CreateMcpClient();
        var body = await PostJsonRpc(client, """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");

        Assert.Contains("\"name\":\"index_documents\"", body);
        Assert.Contains("\"name\":\"search_documents\"", body);
        Assert.Contains("\"name\":\"clear_index\"", body);
    }

    [Fact]
    public async Task Full_flow_index_search_and_clear_tool_calls()
    {
        using var client = CreateMcpClient();

        // 1. Stage a sample markdown document under the fixture's temp directory.
        var testDocsDir = Path.Combine(_factory.TempTestDir, "test_docs");
        Directory.CreateDirectory(testDocsDir);
        var sampleDoc = Path.Combine(testDocsDir, "guide.md");
        await File.WriteAllTextAsync(sampleDoc, "# Developer Guide\nThis guide explains how the system works.");

        // Forward slashes keep the path valid for .NET IO on Windows AND safe as a JSON string
        // literal (no backslash escaping required).
        var cwd = _factory.TempTestDir.Replace("\\", "/");

        // 2. index_documents — full reindex to start from a clean collection.
        var indexRpc = """
            {"jsonrpc":"2.0","id":10,"method":"tools/call","params":{
              "name":"index_documents",
              "arguments":{
                "current_working_directory":"__CWD__",
                "directory":"test_docs",
                "recursive":false,
                "force_reindex":true}}}
            """.Replace("__CWD__", cwd);

        var indexResponse = await PostJsonRpc(client, indexRpc);
        Assert.Contains("Full reindex", indexResponse);

        // 3. search_documents — returns the indexed chunk joined by "---" separators.
        var searchRpc = """
            {"jsonrpc":"2.0","id":11,"method":"tools/call","params":{
              "name":"search_documents",
              "arguments":{"query":"Developer Guide","k":3}}}
            """;

        var searchResponse = await PostJsonRpc(client, searchRpc);
        Assert.Contains("guide.md", searchResponse);

        // 4. clear_index — drops the collection and resets the tracking file.
        var clearRpc = """
            {"jsonrpc":"2.0","id":12,"method":"tools/call","params":{
              "name":"clear_index","arguments":{}}}
            """;

        var clearResponse = await PostJsonRpc(client, clearRpc);
        Assert.Contains("Index cleared", clearResponse);

        // 5. Searching after the clear returns no results.
        var searchAfterClear = await PostJsonRpc(client, searchRpc);
        Assert.Contains("No results found", searchAfterClear);
    }

    [Fact]
    public async Task Di_container_resolves_service_and_indexes_directly()
    {
        // Sanity check of the DI wiring the tools rely on: the host's root provider
        // resolves MarkdownRagService (with the fake embedding pipeline) and a direct
        // invocation indexes its own docs folder (separate from the full-flow test's).
        var service = _factory.Services.GetRequiredService<MarkdownRagMcp.Services.MarkdownRagService>();

        var diDocsDir = Path.Combine(_factory.TempTestDir, "di_docs");
        Directory.CreateDirectory(diDocsDir);
        await File.WriteAllTextAsync(
            Path.Combine(diDocsDir, "di-sample.md"),
            "# DI Sample\nThis file is indexed by the DI sanity check.");

        var result = await service.IndexDocumentsAsync(
            _factory.TempTestDir.Replace("\\", "/"), "di_docs", recursive: false, forceReindex: true);

        Assert.True(result.ProcessedFiles >= 1, $"Indexed {result.ProcessedFiles} file(s)");
        Assert.Contains("di-sample.md", result.Files);
        Assert.Equal("Full reindex", result.Message);
    }
}
