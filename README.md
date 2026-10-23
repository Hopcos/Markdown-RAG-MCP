# MCP-Markdown-RAG (.NET 10)

A **Model Context Protocol (MCP)** server that provides a **local-first RAG engine** for your
markdown documents: it indexes `.md` files into a local vector database and answers semantic
search queries over them. This is a faithful .NET 10 port of
[MCP-Markdown-RAG](https://github.com/Zackriya-Solutions/MCP-Markdown-RAG), preserving the
original's behavior and tool surface while adopting a clean, highly-extensible .NET design.

✅ **Local-first & private** — all data is processed and stored locally. Nothing leaves your
machine for indexing (except the one-time embedding model download).

✅ **Semantic search for markdown** — find document sections by conceptual meaning, not just
keywords.

✅ **MCP-compatible** — works with any MCP host (Claude Desktop, Claude Code, Cursor,
Windsurf, the MCP inspector, …).

✅ **Two transports** — run as a stdio child process (default) or as a Streamable HTTP
service over Kestrel, selected from `appsettings.json`.

✅ **Extensible** — every external concern (embedding, vector storage, chunking, file
discovery, index tracking) sits behind an interface, so alternative providers can be swapped
in via DI without touching call sites.

✅ **Docker & docker compose ready** — one image, environment-variable configuration,
volume-backed index database and model cache (see [Docker deployment](#docker-deployment)).

✅ **Externally-exposed HTTP surface** — with `Transport=Http`, one Kestrel pipeline
serves the MCP Streamable HTTP endpoint together with a Swagger UI (OpenAPI
document), a read-only REST twin of the MCP discovery surface (`/api/tools`), CORS
for browser-hosted frontends, and public discovery/health endpoints.

## Tools

The server exposes three tools, identical in semantics to the original:

| Tool | Description | Arguments |
|------|-------------|-----------|
| `index_documents` | Index markdown files into the local vector database. Incremental by default; `force_reindex=true` rebuilds from scratch. Large corpora are embedded in batches with progress notifications. | `current_working_directory` (required), `directory` (default `""`), `recursive` (default `false`), `force_reindex` (default `false`) |
| `search_documents` | Semantic search over the indexed chunks. Returns the top hits joined by `---` separators, or `"No results found."`. | `query` (required), `k` (default `5`) |
| `clear_index` | Drop the vector collection and reset the tracking file. | — |

Indexing works in the same two modes as the original:

- **Full reindex** (`force_reindex=true`) — drops and rebuilds the whole index.
- **Incremental update** (default) — detects changed files via an md5-hash + modified-time
  tracking file, prunes their old chunks, and re-embeds only them.
- **Recursive indexing** (`recursive=true`) — descends into subdirectories, skipping
  dot-prefixed directories (`.git`, `.obsidian`, …).

The server responds with the same message strings as the Python original:
`"Full reindex"`, `"Incremental update"`, `"Already up to date, Nothing to index!"`,
`"Directory does not exist!"`, and the `---`-separated search output.

## Quick start

### 1. Configure

All configuration is fixed in [`appsettings.json`](src/McpMarkdownRag/appsettings.json) under the `MarkdownRag`
section — there is no command-line surface:

```jsonc
{
  // Optional bearer token protecting EVERY HTTP request (the /mcp endpoint).
  // Non-empty => requests must send "Authorization: Bearer <value>".
  // Environment override: "server-token" (or "ServerToken"). Empty disables auth.
  "server-token": "",

  "MarkdownRag": {
    "Transport": "Stdio",                        // "Stdio" (default) or "Http" (Streamable HTTP)
    "TransportSettings": {
      "HttpEndpointUrl": "http://localhost:3001", // Kestrel bind URL (Transport=Http only)
      "HttpRoutePrefix": "",                     // route prefix, e.g. "/mcp"; "" = root
      "Stateless": true                           // stateless mode (recommended for servers)
    },
    "DatabasePath": "./.db",                      // tracking + vector store files
    "CollectionName": "markdown_vectors",
    "TrackingFileName": "index_tracking.json",
    "VectorStoreFileName": "vectors.json",
    "Embedding": {
      "Provider": "Onnx",                          // DI key of the embedding provider
      "Dimension": 384,                            // must match the configured model
      "ModelId": "sentence-transformers/all-MiniLM-L6-v2",
      "ModelCachePath": "./.models/embeddings",    // first-run download lands here
      "MaxSequenceLength": 256,
      "PoolingMode": "Mean"
    },
    "Chunking": { "ChunkSize": 512, "ChunkOverlap": 100 },
    "Search":   { "DefaultLimit": 5 },
    "Indexing":  { "BatchSize": 32 }               // documents embedded per batch
  }
}
```

> **Environment variables** — every config value can be overridden with the standard
> `__`-segment notation (handy for Docker), e.g.:
>
> ```bash
> MarkdownRag__Transport="Http"
> MarkdownRag__TransportSettings__HttpEndpointUrl="http://0.0.0.0:8080"
> MarkdownRag__TransportSettings__HttpRoutePrefix="mcp"
> MarkdownRag__TransportSettings__Stateless="true"
> MarkdownRag__DatabasePath="/app/.db"
> MarkdownRag__Embedding__ModelCachePath="/app/.models/embeddings"
> ServerToken="my-secret"        # Windows-friendly alias of root-level "server-token"
> ```

> **First run** — the embedding model (`model.onnx` + `vocab.txt`, ~90 MB) is downloaded
> automatically from Hugging Face and cached under `ModelCachePath`. A **background preload**
> hosted service fetches the model and builds the ONNX session right after startup, so the
> first tool call does not pay that cost; if a tool call arrives before the preload finishes,
> it simply waits for the load to complete. To use a pre-downloaded model instead, place
> both files into the cache directory manually:
>
> ```
> .models/embeddings/sentence-transformers/all-MiniLM-L6-v2/
> ├── model.onnx
> └── vocab.txt
> ```

> **Transport selection** —
> - `"Stdio"` (default) — JSON-RPC over stdin/stdout; the server runs as a child process of
>   the MCP host (Claude Desktop, Cursor, Claude Code, …). Logging is forced to stderr so
>   the protocol stream is never corrupted.
> - `"Http"` — Streamable HTTP over Kestrel at `HttpEndpointUrl`, mounted at
>   `HttpRoutePrefix` (e.g. `/mcp`). `Stateless=true` is recommended; set it false only if a
>   client needs server-initiated requests (sampling/elicitation), which this server does
>   not issue. Any setting under `TransportSettings` is ignored when `Transport=Stdio`.

### 2. Run

```bash
dotnet run --project src/McpMarkdownRag   # stdio server (default)
# or
dotnet publish src/McpMarkdownRag -c Release -r win-x64 --self-contained false
# exe: bin/Release/net10.0/win-x64/publish/mcp-markdown-rag.exe
```

The exe resolves `appsettings.json` from its own directory, so place a configured copy next
to the binary.

Launch profiles work too, and the transport decision is environment-aware:

```bash
dotnet run -lp hopcos                      # ASPNETCORE_ENVIRONMENT=hopcos from the profile
```

The profile's `ASPNETCORE_ENVIRONMENT` selects the environment-specific
`appsettings.<environment>.json` (e.g. `appsettings.hopcos.json` with `Transport=Http`), so
the command boots the externally-exposed HTTP surface — Kestrel prints the bound URL
("Now listening on: http://localhost:3001"). Note the precedence: `ASPNETCORE_ENVIRONMENT`
(the variable launch profiles set) wins over `DOTNET_ENVIRONMENT`, and environment
variables override the JSON files, so `MarkdownRag__Transport=Stdio` would switch back.

### 3. Register with an MCP host

#### Claude Code (stdio, pointing at the built .exe)

```json
{
  "mcpServers": {
    "markdown_rag": {
      "command": "E:\\path\\to\\mcp-markdown-rag.exe",
      "args": []
    }
  }
}
```

On Linux/macOS use a forward-slash path to the binary.

#### Claude Desktop / Cursor (stdio via dotnet run)

```json
{
  "mcpServers": {
    "markdown_rag": {
      "command": "dotnet",
      "args": ["run", "--project", "/ABSOLUTE/PATH/TO/Markdown-RAG-MCP-Dotnet/src/McpMarkdownRag", "-c", "Release"]
    }
  }
}
```

#### HTTP hosts (Streamable HTTP transport)

Set `Transport=Http` and point an HTTP-capable MCP host at the endpoint, e.g. Claude Desktop:

```json
{
  "mcpServers": {
    "markdown_rag": {
      "url": "http://localhost:3001/mcp"
    }
  }
}
```

When the server requires a token (see [Authentication](#authentication)), HTTP hosts deliver
it via a header, e.g. Claude Code:

```json
{
  "mcpServers": {
    "markdown_rag": {
      "type": "http",
      "url": "http://localhost:3001/mcp",
      "headers": { "Authorization": "Bearer my-secret" }
    }
  }
}
```

### Authentication

When the root-level `server-token` configuration value is **non-empty**, every HTTP request
(except CORS preflights and the intentionally public paths — the Swagger UI and its JSON
document under `/swagger`, the root discovery endpoint `/`, and the health check `/health`)
must present the token; when it is empty, authentication is disabled entirely:

```bash
curl -H "Authorization: Bearer my-secret" http://localhost:3001/mcp
```

Credentials are accepted as `Authorization: Bearer <token>` or, for browser-issued GET
requests, as `?access_token=<token>`. A missing or incorrect credential yields
`401 Unauthorized` with `WWW-Authenticate: Bearer` and the payload
`{"error":"UNAUTHORIZED","message":"Invalid authorization: ..."}`. Token comparison is
constant-time (SHA-256 digests, no timing side-chains).

> Configuration lives at the appsettings root level: `"server-token": ""`. It can be
> overridden with the environment variables `server-token` (Docker/Linux) or `ServerToken`
> (Windows hosts, where env-var names cannot contain a dash).

### HTTP web surface

With `Transport=Http` the same Kestrel pipeline serves the MCP endpoint and an
externally-exposed REST surface:

| Path | Auth | Purpose |
|------|------|---------|
| `/mcp` (prefix configurable) | Required when `server-token` is set | The MCP Streamable HTTP endpoint (initialize, tools/list, tool calls) |
| `/` | Public | Server discovery info: name, transport, MCP path, stateless mode, tool names, surface links |
| `/health` | Public | Health check — answers `{"status":"ok"}` |
| `/swagger` | Public | Swagger UI over the OpenAPI document (`/swagger/v1/swagger.json`); the Authorize dialog supplies `Bearer <server-token>` for authenticated calls |
| `/api/tools` | Required when `server-token` is set | Read-only REST twin of the MCP discovery surface: the three tools with descriptions and typed parameter rows, resolved live from the same `[McpServerTool]` annotations `tools/list` uses |

Browser-hosted frontends integrate over CORS (`AllowAnyOrigin/Method/Header`). Unhandled
exceptions answer with the uniform error contract `{"error":"<code>","message":"..."}`:
malformed requests → `400 BAD_REQUEST`, timeouts → `504 TIMEOUT`, anything else →
`500 INTERNAL_ERROR`.

### 4. Debug with the MCP inspector

```bash
npx @modelcontextprotocol/inspector
```

Add a server, pick the matching **Transport Type** (stdio: command + args, or
**STREAMABLE HTTP**: URL), and connect. The server reports `mcp-markdown-rag` with the three
tools above.

> ⏱️ **Large corpora & client timeouts.** Embedding thousands of chunks on CPU can exceed a
> client's default request timeout (the inspector cancels after 60 s). Raise the inspector's
> **Request Timeout** (Settings panel) for big indexing runs — the server also emits
> `notifications/progress` after each embedding batch, which clients that honor progress
> notifications reset their idle timer on.

### 5. Ship only the vector index to a query-only server

Searching never reads the original markdown files: every persisted vector record embeds
its chunk **text** and source **file name** inside `vectors.json`, and `search_documents`
answers entirely from the vector store. Neither the `.md` sources nor the incremental
tracking file (`index_tracking.json`) are needed on the querying machine.

```bash
# 1. Index locally as usual.
dotnet run -lp hopcos   # http://localhost:3001 → index_documents

# 2. Copy ONLY the persisted index to the remote server (same relative location):
#    <DatabasePath>/vectors.json   (default: ./.db/vectors.json)

# 3. Start the server on the remote box — searches work immediately.
dotnet run -lp hopcos
```

What to copy | What NOT to copy
---|---
`vectors.json` (the whole `DatabasePath` directory is also fine) | source `.md` files, `index_tracking.json`

The remote box still needs the things query semantics depend on: the **embedding model
cache** (`.models/`, the same model used at indexing time — the query text is embedded
with it) and a `Transport=Http` serving configuration. Do **not** call `index_documents` on
the query-only server: without source files it answers `Directory does not exist!`, and the
incremental-update feature only exists where the sources live.

### 6. Hosting on IIS and troubleshooting

The app runs in-process under IIS (ASP.NET Core Module V2, `hostingModel="inprocess"` in
`web.config`). Publish framework-dependent (`dotnet publish -c Release -r win-x64
--self-contained false`) and make sure the server has the **.NET 10 Hosting Bundle**.
Notes that apply on any IIS box:

- **Port**: under IIS the *site binding* decides the listening port; `HttpEndpointUrl` in
  `appsettings.json` is ignored.
- **Relative paths** (`DatabasePath`, `ModelCachePath`, `./.db`, `./.models`) resolve against
  the process working directory, which is the site's physical path. Keep `.db/` and
  `.models/` beside `web.config`, or use absolute paths.
- **Keep `runtimes\win-x64`** when trimming the publish output — the server is x64, and
  `runtimes\win-x64\native\onnxruntime.dll` is what is loaded. Only the other RIDs
  (linux/osx/arm64/win-x86) may be removed.
- **Copy order for the index**: stop the app (drop an `app_offline.htm`), copy
  `vectors.json`, then start it again. (Versions before `e8e8e8e` overwrote the on-disk
  index with `[]` when a process that had loaded an empty store was recycled; the store now
  never flushes an empty state on shutdown.) The file must be named exactly
  `vectors.json` (`VectorStoreFileName`) — a renamed copy such as `vectors1.json` is ignored.
- **Diagnostics**: set `stdoutLogEnabled="true"` in `web.config` (editing it recycles the
  site), reproduce the call, and read `logs\stdout_*.log`; turn it off again afterwards.
  The MCP client only sees the generic `An error occurred invoking 'search_documents'`; the
  real exception is in that log.

#### Lesson learned: "works on my machine, `isError` on the server" — missing VC++ runtime

Symptom: identical files (same dll / `model.onnx` / `vocab.txt` / `vectors.json` hashes), the
site starts, `/health` and `initialize` are fine, but **every** `search_documents` /
`index_documents` call returns `isError: true`. The stdout log shows:

```
System.TypeInitializationException: The type initializer for
'Microsoft.ML.OnnxRuntime.NativeMethods' threw an exception.
 ---> System.NullReferenceException ... at System.SpanHelpers.Memmove ...
```

Root cause: the embedding model runs on the native `onnxruntime.dll` (1.20), which needs the
**Microsoft Visual C++ 2015-2022 Redistributable (x64)**. A fresh Windows Server image does
not have it (`System32\vcruntime140.dll`, `vcruntime140_1.dll`, `msvcp140*.dll` missing).
The native 1.20 library then fails to load, .NET silently falls back to the old
Windows-bundled `C:\Windows\System32\onnxruntime.dll` (v1.5), and its API does not match the
managed 1.20 binding, so the static initializer throws. Developer machines almost always have
the redistributable (installed by Visual Studio), which is why it "works locally".

Fix: install `vc_redist.x64.exe` (<https://aka.ms/vs/17/release/vc_redist.x64.exe>) on the
server, then **recycle the app pool / touch `web.config`** — the failed static initializer is
cached for the life of the process, so already-running workers stay broken until restarted.

Quick check on the server (all three should print `True`):

```powershell
'vcruntime140.dll','vcruntime140_1.dll','msvcp140.dll' |
  ForEach-Object { Test-Path "$env:WINDIR\System32\$_" }
```

Things that did **not** help (do not chase them): trimming `runtimes\`, ACLs on the site
folder, copying the VC dlls beside the app or into `runtimes\win-x64\native`, or copying
`onnxruntime.dll` into the site root. Only a system-wide VC++ runtime fixes it.

### Example tool calls

**Index a folder (incremental, recursive):**

```json
{
  "current_working_directory": "/home/me/notes",
  "directory": "",
  "recursive": true,
  "force_reindex": false
}
```

**Force a full reindex of a subdirectory:**

```json
{
  "current_working_directory": "/home/me",
  "directory": "projects/wiki",
  "recursive": true,
  "force_reindex": true
}
```

**Search the indexed content:**

```json
{ "query": "How does the auth middleware refresh tokens?", "k": 5 }
```

**Clear the index:**

```json
{ }
```

## Docker deployment

Build the image and run it either manually or via compose. The container runs the **HTTP**
transport; the index database and the model cache are volume-backed so they survive
recreation.

### docker compose (recommended)

```bash
docker compose up -d --build
```

`docker-compose.yml` configures everything through environment variables:

| Variable | Value | Purpose |
|----------|-------|---------|
| `MarkdownRag__Transport` | `Http` | Streamable HTTP inside the container |
| `MarkdownRag__TransportSettings__HttpEndpointUrl` | `http://0.0.0.0:8080` | Bind every interface |
| `MarkdownRag__TransportSettings__HttpRoutePrefix` | `mcp` | MCP endpoint at `/mcp` |
| `MarkdownRag__TransportSettings__Stateless` | `true` | Independent HTTP requests |
| `MarkdownRag__DatabasePath` | `/app/.db` | Volume-backed index database |
| `MarkdownRag__Embedding__ModelCachePath` | `/app/.models/embeddings` | Volume-backed model cache |
| `server-token` | `${MCP_SERVER_TOKEN:-}` | Optional bearer token guarding every `/mcp` request (empty disables auth) |

Volumes (created under the compose project directory):

- `./db` → `/app/.db` — `index_tracking.json` + `vectors.json` survive restarts.
- `./hf-models` → `/app/.models/embeddings` — the ONNX model is downloaded once; the volume
  prevents re-downloading when the container is recreated.
- `./docs` → `/data/docs` (optional, commented out) — mount your markdown corpora so
  `index_documents` can index them with
  `current_working_directory=/data/docs` (+ `directory=<subdir>`).

The MCP endpoint after deployment: `http://<host>:8080/mcp`. When a token is configured,
verify with `curl -H "Authorization: Bearer $MCP_SERVER_TOKEN" http://localhost:8080/mcp`.

### Manual docker build

```bash
docker build -t mcp-markdown-rag:latest .
docker run -d --name mcp-markdown-rag -p 8080:8080 \
  -e MarkdownRag__Transport=Http \
  -e MarkdownRag__TransportSettings__HttpEndpointUrl=http://0.0.0.0:8080 \
  -e MarkdownRag__TransportSettings__HttpRoutePrefix=mcp \
  -e MarkdownRag__TransportSettings__Stateless=true \
  -e MarkdownRag__DatabasePath=/app/.db \
  -e MarkdownRag__Embedding__ModelCachePath=/app/.models/embeddings \
  -e server-token=my-secret \
  -v "$PWD/db:/app/.db" \
  -v "$PWD/hf-models:/app/.models/embeddings" \
  -v "$PWD/docs:/data/docs" \
  mcp-markdown-rag:latest
```

> The Dockerfile publishes with `/p:UseAppHost=false /p:DebugType=None` (dll entrypoint,
> no PDBs) and the runtime stage is `mcr.microsoft.com/dotnet/aspnet:10.0`, matching the
> Kestrel-based Streamable HTTP transport. `.dockerignore` excludes `bin/`, `obj/`,
> `tests/`, `.db/`, `.models/` and docs so the build context stays small and the image
> carries no pre-existing index or model.

## Tests

The test suite boots the real application pipeline and covers the full link —
unit level (chunker, storage, orchestration) and integration level (the real `/mcp`
endpoint over TestServer):

| Area | File | What it asserts |
|------|------|-----------------|
| Unit | `tests/McpMarkdownRag.Tests/Unit/MarkdownChunkerTests.cs` | Heading split + token re-split with overlap, empty input handling |
| Unit | `tests/McpMarkdownRag.Tests/Unit/StorageTests.cs` | md5+mtime fingerprinting, JSON tracking persistence, file discovery (dot-prefixed dirs skipped in recursive mode), vector store insert/search/delete/drop |
| Unit | `tests/McpMarkdownRag.Tests/Unit/MarkdownRagServiceTests.cs` | Full lifecycle with a deterministic fake embedding provider + in-memory mock filesystem: nonexistent directory message, incremental → nothing-to-do, search output, full reindex, clear |
| Unit | `tests/McpMarkdownRag.Tests/Security/BearerTokenMiddlewareTests.cs` | Token gate disabled when `server-token` is empty/missing; bearer header + `?access_token=` accepted; wrong/malformed/missing credentials → 401 contract payload; CORS preflight exempt; public-path exemptions (Swagger UI/document, `/`, `/health`) pass without credentials; lookalike paths stay guarded; constant-time comparison |
| Integration | `tests/McpMarkdownRag.Tests/Integration/McpIntegrationTests.cs` | Real `/mcp` Streamable HTTP contract: `initialize` (serverInfo + instructions), `tools/list` (three tools), the complete `index_documents` → `search_documents` → `clear_index` flow with the original message strings, plus a DI-wiring sanity check |
| Integration | `tests/McpMarkdownRag.Tests/Integration/AuthTokenIntegrationTests.cs` | Auth-enabled server fixture: credentials-less and wrong-token MCP requests → 401 with the contract payload; valid bearer token initializes the session and reaches `tools/list`; the `/api/tools` twin answers 401 without credentials while `/`, `/health` and `/swagger` stay public |
| Integration | `tests/McpMarkdownRag.Tests/Integration/WebSurfaceIntegrationTests.cs` | Public HTTP surface end-to-end: root info + `/health` payload, public Swagger document describing `/api/tools`, the live `/api/tools` catalog (three tools, typed parameter rows with required/optional), and the environment-aware transport decision (`ASPNETCORE_ENVIRONMENT=hopcos` with no Transport override → `appsettings.hopcos.json`'s `Transport=Http` boots the HTTP surface) |
| Integration | `tests/McpMarkdownRag.Tests/Integration/PortableIndexIntegrationTests.cs` | Index portability: after indexing, copying **only** `vectors.json` to a fresh server directory (no markdown sources, no `index_tracking.json`) is enough for `search_documents` to return the file name and full chunk text |

The integration fixture swaps the ONNX pipeline for a deterministic
`FakeEmbeddingProvider` (and the BERT tokenizer for a lightweight one), so no model download
or network access happens during tests. Run everything:

```bash
dotnet test tests/McpMarkdownRag.Tests
```

Code coverage collection is wired via `coverlet.collector`:

```bash
dotnet test tests/McpMarkdownRag.Tests --collect "XPlat,Code Coverage"
```

## Project layout

| Area | File(s) | Purpose |
|------|---------|---------|
| Host | `src/McpMarkdownRag/Program.cs` | Host + DI wiring; stdio *or* Streamable HTTP transport, logging→stderr |
| Tools | `src/McpMarkdownRag/Tools/MarkdownRagTools.cs` | Thin MCP tool adapters (`[McpServerTool]` methods) |
| Orchestration | `src/McpMarkdownRag/Services/MarkdownRagService.cs` | Indexing/search orchestration |
| Preload | `src/McpMarkdownRag/Services/EmbeddingPreloadHostedService.cs` | Background model preload at startup |
| Abstractions | `src/McpMarkdownRag/Abstractions/*.cs` | `IEmbeddingProvider`, `IVectorStore`, `IMarkdownChunker`, `IIndexTracker`, `IMarkdownFileDiscovery`, `ITextTokenizer` |
| Chunking | `src/McpMarkdownRag/Chunking/MarkdownChunker.cs` | Heading split → token split (512 / 100 overlap) |
| Embeddings | `src/McpMarkdownRag/Embeddings/*.cs` | ONNX embedding provider + BERT tokenizer + model downloader |
| Storage | `src/McpMarkdownRag/Storage/*.cs` | In-memory vector store, JSON index tracker, file discovery |
| Security | `src/McpMarkdownRag/Security/BearerTokenMiddleware.cs` | Optional `server-token` bearer gate over every HTTP request (public-path exemptions included) |
| API | `src/McpMarkdownRag/Api/MarkdownRagApiEndpoints.cs` | Read-only REST twin of the MCP discovery surface (`/api/tools`) |
| Config | `src/McpMarkdownRag/Configuration/`, `src/McpMarkdownRag/appsettings.json` | Strongly-typed appsettings options |
| Tests | `tests/McpMarkdownRag.Tests/` | Unit + full-link integration tests (TestServer) |
| Docker | `Dockerfile`, `docker-compose.yml`, `.dockerignore` | Container deployment |

## How the design principles are applied

**High extensibility** — every external dependency sits behind an interface and is
registered exactly once in `Program.cs` (DI). Swapping the in-memory store for a Milvus
client, the ONNX embeddings for a cloud embedding API, or the chunker for an alternative
costs a single DI registration change. The tools and the orchestration service never
reference a concrete implementation.

**Configuration only in appsettings** — there is no command-line surface. `appsettings.json`
controls the transport (stdio vs. HTTP), the HTTP bind URL/route, the database path,
collection name, embedding model + dimension, chunk size/overlap, default search limit, and
batch size. Tool arguments (`directory`, `recursive`, `force_reindex`, `k`) keep the
original defaults.

**Functional parity** — same three tools, same arguments and defaults, same
incremental-vs-full reindex semantics, same md5+mtime change detection, same
`---`-separated search output, same message strings, and the same server name/version/
instructions string as the Python original.

**Local-first** — the original embeds Milvus Lite (Python-only) and pymilvus's bundled
embedding function; both are replaced with local-first defaults (a JSON-persisted in-memory
vector store and an ONNX embedding provider) that preserve the privacy guarantee. The ONNX
model is downloaded from Hugging Face on first run and cached locally, mirroring the
original's "first run downloads the model" experience.

## Differences from the Python original

- **Vector store** — the original embeds Milvus Lite (Python-only). This port uses an
  in-memory vector store persisted to JSON, hidden behind `IVectorStore`; implementing that
  interface wires in Milvus or any other vector DB.
- **Embeddings** — the original uses pymilvus's bundled `DefaultEmbeddingFunction`. This
  port runs a Hugging Face sentence-transformer exported to ONNX through
  `Microsoft.ML.OnnxRuntime` (CPU provider by default; CUDA/DirectML extensions can be
  swapped in via DI without touching call sites), with mean-pooling + L2 normalization —
  the same post-processing as the original. Swappable via `IEmbeddingProvider`.
- **Chunking** — the original uses LlamaIndex's `MarkdownNodeParser` +
  `TokenTextSplitter`. This port replicates the two-stage pipeline in managed code (split
  by heading first, then re-split by token size with overlap), token-counted with the
  model's BERT tokenizer.
- **Dot-dir skipping is enforced by the server itself** — recursive enumeration filters
  dot-prefixed directory segments (`EnumerationOptions` has no dot-dir flag), guaranteeing
  the original behavior on Windows, Linux and macOS alike.

## License

Apache 2.0, matching the upstream project.

---


[中文版本](README.zh.md)
