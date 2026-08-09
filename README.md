# MCP-Markdown-RAG (.NET 8 port) / MCP-Markdown-RAG（.NET 8 移植版）

[English](#english) · [中文](#中文)

---

## English

A **Model Context Protocol (MCP)** server that provides a **local-first RAG engine** for your
markdown documents. This is a faithful .NET 8 reimplementation of
[MCP-Markdown-RAG](https://github.com/Zackriya-Solutions/MCP-Markdown-RAG), preserving the
original's behavior and tool surface while adopting a clean, highly-extensible .NET design.

✅ **Local-first & private** — all data is processed and stored locally. Nothing leaves your
machine for indexing.

✅ **Semantic search for markdown** — find document sections by conceptual meaning, not just
keywords.

✅ **MCP-compatible** — works with any MCP host (Claude Desktop, Windsurf, Cursor, …).

✅ **Extensible** — every external concern (embedding, vector storage, chunking, file
discovery, index tracking) sits behind an interface, so alternative providers can be swapped in
via DI without touching call sites.

### Tools

The server exposes three tools, identical in semantics to the original:

| Tool | Description | Arguments |
|------|-------------|-----------|
| `index_documents` | Index markdown files into the local vector database. Incremental by default; `force_reindex=true` rebuilds from scratch. | `current_working_directory` (required), `directory` (default `""`), `recursive` (default `false`), `force_reindex` (default `false`) |
| `search_documents` | Semantic search over the indexed chunks. | `query` (required), `k` (default `5`) |
| `clear_index` | Drop the vector collection and reset the tracking file. | — |

Indexing works in the same two modes as the original:

- **Full reindex** (`force_reindex=true`) — drops and rebuilds the whole index.
- **Incremental update** (default) — detects changed files via an md5-hash + modified-time
  tracking file, prunes their old chunks, and re-embeds only them.
- **Recursive indexing** (`recursive=true`) — descends into subdirectories, skipping
  dot-prefixed directories.

### Project layout

| Area | File(s) | Purpose |
|------|---------|---------|
| Host | `Program.cs` | Host + DI wiring (stdio transport, logging→stderr) |
| Tools | `Tools/MarkdownRagTools.cs` | Thin MCP tool adapters (`[McpServerTool]` methods) |
| Orchestration | `Services/MarkdownRagService.cs` | Indexing/search orchestration |
| Abstractions | `Abstractions/*.cs` | `IEmbeddingProvider`, `IVectorStore`, `IMarkdownChunker`, `IIndexTracker`, `IMarkdownFileDiscovery`, `ITextTokenizer` |
| Chunking | `Chunking/MarkdownChunker.cs` | Heading split → token split (512 / 100 overlap) |
| Embeddings | `Embeddings/*.cs` | ONNX embedding provider + BERT tokenizer + model downloader |
| Storage | `Storage/*.cs` | In-memory vector store, JSON index tracker, file discovery |
| Config | `Configuration/`, `appsettings.json` | Strongly-typed appsettings options |

### How the design principles are applied

**High extensibility** — every external dependency sits behind an interface and is registered
exactly once in `Program.cs` (DI). Swapping the in-memory store for a Milvus client, the
ONNX embeddings for a cloud embedding API, or the chunker for an alternative costs a single DI
registration change. The tools and the orchestration service never reference a concrete
implementation.

**Configuration only in appsettings** — there is no command-line surface. `appsettings.json`
controls the database path, collection name, embedding model + dimension, chunk size/overlap,
default search limit, and batch size. Tool arguments (`directory`, `recursive`,
`force_reindex`, `k`) keep the original defaults.

**Functional parity** — same three tools, same arguments and defaults, same incremental-vs-full
reindex semantics, same md5+mtime change detection, same `---`-separated search output, same
"Already up to date" / "Directory does not exist" messages, and the same server name/version/
instructions string as the Python original.

**Local-first** — the original embeds Milvus Lite (Python-only) and pymilvus's bundled
embedding function; both are replaced with local-first defaults (a JSON-persisted in-memory
vector store and an ONNX embedding provider) that preserve the privacy guarantee. The ONNX
model is downloaded from Hugging Face on first run and cached locally, mirroring the original's
"first run downloads the model" experience.

### Configuration

All configuration is fixed in [`appsettings.json`](appsettings.json) under the `MarkdownRag`
section:

```jsonc
{
  "MarkdownRag": {
    "DatabasePath": "./.db",                 // tracking + vector store files
    "CollectionName": "markdown_vectors",
    "TrackingFileName": "index_tracking.json",
    "VectorStoreFileName": "vectors.json",

    "Embedding": {
      "Provider": "Onnx",                    // DI key for the embedding provider
      "Dimension": 768,                      // must match the configured model
      "ModelId": "sentence-transformers/all-MiniLM-L6-v2",
      "ModelCachePath": "./.models/embeddings",// first-run download lands here
      "MaxSequenceLength": 256,
      "PoolingMode": "Mean"
    },
    "Chunking": { "ChunkSize": 512, "ChunkOverlap": 100 },
    "Search":   { "DefaultLimit": 5 },
    "Indexing":  { "BatchSize": 32 }          // documents embedded per batch
  }
}
```

> **First run:** the embedding model (~90 MB) is downloaded automatically from Hugging Face
> and cached under `ModelCachePath`. To avoid a first-call timeout, the server starts a
> **background preload** at startup: it fetches the model and builds the ONNX session while
> the MCP handshake proceeds. If a tool call arrives before the preload finishes, it simply
> waits for the load to complete. Tool calls are also **batched** into a single ONNX forward
> pass, keeping first-call latency well under client timeouts.
>
> **Model placement (manual / offline):** if you pre-download the model yourself, place the
> two files into the cache directory below (create it if missing). The server skips the
> download when both files are present.
>
> ```
> .models/embeddings/sentence-transformers/all-MiniLM-L6-v2/
> ├── model.onnx   ← from huggingface.co/.../onnx/model.onnx (~90 MB)
> └── vocab.txt    ← from huggingface.co/.../vocab.txt
> ```
>
> The path is `<ModelCachePath>/<ModelId-with-slashes-turned-into-folder-separators>/`. With the
> defaults above and the exe run from its own directory, that is
> `./.models/embeddings/sentence-transformers/all-MiniLM-L6-v2/`. If you change `ModelId`,
> the subfolder name changes accordingly.

### Install & run

Publish a self-contained executable:

```bash
dotnet publish -c Release -r win-x64 --self-contained false
# exe: bin/Release/net8.0/win-x64/publish/mcp-markdown-rag.exe
```

(For a fully self-contained build that needs no .NET runtime installed, use `--self-contained true`.)

### Configure your MCP host

#### Claude Code (pointing at the built .exe)

For Claude Code, edit `.mcp.json` (project or user scope) and point the `command` directly at
the built executable — no `dotnet run` needed:

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

> The exe resolves `appsettings.json` from its own directory, so place a configured
> `appsettings.json` next to `mcp-markdown-rag.exe`. On Linux/macOS use the forward-slash
> path to the `mcp-markdown-rag` binary.

#### Claude Desktop / Windsurf / Cursor (dotnet run)

```json
{
  "mcpServers": {
    "markdown_rag": {
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "/ABSOLUTE/PATH/TO/markdown_rag_mcp_dotnet",
        "-c",
        "Release"
      ]
    }
  }
}
```

#### Debug with the MCP inspector

Launch the inspector UI:

```bash
npx @modelcontextprotocol/inspector
```

In the inspector, open **Add Server** and fill in the fields. This server is a local stdio
process, so set **Transport Type** to `STDIO`. The remaining fields:

| Field | What to enter |
|-------|---------------|
| **Transport Type** | `STDIO` |
| **Command** | The executable that launches the server (see options below) |
| **Arguments** | Arguments passed to the command, one per line. Leave empty when using the `.exe` directly. |
| **Environment Variables** | Extra env vars (key/value). Not required by this server — leave empty. |

Pick one of these three launch styles:

**A. Direct `.exe` (fastest, recommended)** — requires `dotnet publish` first (see
*Install & run*), and a configured `appsettings.json` beside the exe.

- **Command**: `E:\path\to\mcp-markdown-rag.exe`
- **Arguments**: *(empty)*

**B. `dotnet run` (no publish needed, handy during development)**

- **Command**: `dotnet`
- **Arguments** (one per line):
  ```
  run
  --project
  E:\path\to\markdown_rag_mcp_dotnet
  -c
  Release
  ```

**C. `dotnet` pointing at the built dll**

- **Command**: `dotnet`
- **Arguments** (one per line):
  ```
  E:\path\to\markdown_rag_mcp_dotnet\bin\Release\net8.0\mcp-markdown-rag.dll
  ```

Click **Connect**. The lower-left panel should show the server name `mcp-markdown-rag` and the
three tools (`index_documents`, `search_documents`, `clear_index`).

> ⚠️ **First tool call may still wait for the model.** The server begins preloading the model
> (~90 MB) in the background at startup, right after the `initialize` handshake. If you call
> a tool before that preload finishes, the call waits for it — this can look like a hang on the
> very first run. Watch the inspector's log panel (it logs `Embedding model preload complete.`);
> once loaded, all calls return promptly and the model is cached on disk for next time.

> ⏱️ **Raising the inspector's request timeout for large corpora.** The MCP inspector cancels
> a tool call after **60 000 ms (60 s)** by default — embedding thousands of chunks on CPU can
> take longer than that. To index large folders through the inspector you must raise its
> **Request Timeout**:
> - In the inspector UI, open the **Settings** panel (top toolbar) and set **Request Timeout**
>   to **600000** (10 minutes = 60 000 × 10). `0` means "SDK default" (60 s) — set a concrete
>   number, not 0.
> - The value is stored under `~/.mcp-inspector/storage/` (and in the browser's `localStorage`
>   key `mcp-inspector` for the web UI), so it persists across sessions.
> - The server cannot change this: it is the *client's* request timeout, and a JSON-RPC server
>   has no way to force a client to wait longer. The server does emit `notifications/progress`
>   during indexing (see below), which clients that honor progress use to reset their idle
>   timer — but the inspector's hard request timeout still applies, so raise it for big runs.
> - Alternative: index smaller subtrees, or call `index_documents` from a client with a higher
>   / no timeout (e.g. a small script over stdio).

### Using the tools directly

Once the server is registered with a host, the LLM calls these tools for you. You can also
drive them directly for manual indexing — e.g. when you want to index a folder of markdown
notes on demand. The fastest way is the MCP inspector (`npx @modelcontextprotocol/inspector`),
which gives you a UI to call each tool. Example tool calls:

**Index a folder (incremental, recursive):**

```json
{
  "current_working_directory": "/home/me/notes",
  "directory": "",
  "recursive": true,
  "force_reindex": false
}
```

`current_working_directory` is the base path; `directory` is resolved relative to it (use `""`
to index the base itself). Returns something like:

```json
{
  "message": "Incremental update",
  "processedFiles": 3,
  "totalChunks": 42,
  "files": ["design.md", "roadmap.md", "meeting-notes.md"],
  "mode": "Incremental update"
}
```

**Force a full rebuild of a subfolder:**

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
{ "query": "how does the auth middleware refresh tokens?", "k": 5 }
```

**Clear the index:**

```json
{}
```

> The first `index_documents` (or `search_documents`) call triggers the one-time model
> download, so expect a delay on first use. Subsequent calls use the cached model.

> ⏱️ **Large corpora & request timeouts.** Embedding thousands of chunks on CPU can take
> several minutes, longer than many clients' default request timeout (the MCP inspector uses
> ~60 s). To prevent the client from cancelling mid-index, `index_documents` reports
> `notifications/progress` after every embedding batch when the client supplies a progress
> token. Clients that honor progress notifications (the inspector does, when you enable
> progress in its settings) reset their idle timer on each update and keep the call alive until
> it completes. If your client does not send a progress token, the server simply runs without
> notifications — for very large corpora on such a client, prefer indexing smaller subtrees or
> raising the client's timeout.

### Differences from the Python original

- **Vector store** — the original embeds Milvus Lite (Python-only). This port uses an
  in-memory store persisted as JSON behind the `IVectorStore` abstraction, preserving the
  local-first philosophy. A Milvus-backed implementation can be added by implementing the
  interface.
- **Embeddings** — the original uses pymilvus's bundled `DefaultEmbeddingFunction`. This
  port runs a Hugging Face sentence-transformer model exported to ONNX via
  `Microsoft.ML.OnnxRuntime`, with mean-pooling and L2 normalization (the same
  post-processing). Provider is swappable via `IEmbeddingProvider`.
- **Chunking** — the original uses LlamaIndex's `MarkdownNodeParser` + `TokenTextSplitter`.
  This port reproduces that two-stage pipeline (heading-based split, then token-size split with
  overlap) in managed code, using the model's own BERT tokenizer for token counts.

### License

Apache 2.0, matching the upstream project.

---

## 中文

一个 **Model Context Protocol (MCP)** 服务器，为你的 Markdown 文档提供**本地优先的 RAG
引擎**。本项目是
[MCP-Markdown-RAG](https://github.com/Zackriya-Solutions/MCP-Markdown-RAG) 的 .NET 8 忠实复刻，
保留了原项目的行为与工具接口，同时采用清晰、高可扩展的 .NET 设计。

✅ **本地优先、隐私安全** —— 所有数据在本地处理与存储，索引过程不会把任何内容发送到第三方服务。

✅ **Markdown 语义搜索** —— 按语义含义而非关键字查找文档片段。

✅ **兼容 MCP** —— 可接入任何支持 MCP 的宿主（Claude Desktop、Windsurf、Cursor 等）。

✅ **高可扩展** —— 所有外部依赖（嵌入、向量存储、分块、文件发现、索引追踪）都隐藏在接口背后，
通过 DI 替换实现即可，调用方无需改动。

### 工具（Tools）

服务器暴露三个工具，语义与原项目完全一致：

| 工具 | 说明 | 参数 |
|------|------|------|
| `index_documents` | 将 Markdown 文件索引进本地向量库。默认增量索引；`force_reindex=true` 时全量重建。 | `current_working_directory`（必填）、`directory`（默认 `""`）、`recursive`（默认 `false`）、`force_reindex`（默认 `false`） |
| `search_documents` | 对已索引的片段做语义搜索。 | `query`（必填）、`k`（默认 `5`） |
| `clear_index` | 清空向量集合并重置追踪文件。 | 无 |

索引模式与原项目一致：

- **全量重建**（`force_reindex=true`）—— 清空并从零重建整个索引。
- **增量更新**（默认）—— 通过 md5 哈希 + 修改时间的追踪文件检测变更文件，删除其旧分块后只重新嵌入这些文件。
- **递归索引**（`recursive=true`）—— 递归进入子目录，跳过以点开头的目录。

### 项目结构

| 区域 | 文件 | 作用 |
|------|------|------|
| 宿主 | `Program.cs` | 宿主 + DI 装配（stdio 传输、日志输出到 stderr） |
| 工具 | `Tools/MarkdownRagTools.cs` | 精简的 MCP 工具适配器（`[McpServerTool]` 方法） |
| 编排 | `Services/MarkdownRagService.cs` | 索引/搜索的编排逻辑 |
| 抽象 | `Abstractions/*.cs` | `IEmbeddingProvider`、`IVectorStore`、`IMarkdownChunker`、`IIndexTracker`、`IMarkdownFileDiscovery`、`ITextTokenizer` |
| 分块 | `Chunking/MarkdownChunker.cs` | 按标题切分 → 按 token 切分（512 / 100 重叠） |
| 嵌入 | `Embeddings/*.cs` | ONNX 嵌入提供者 + BERT 分词器 + 模型下载 |
| 存储 | `Storage/*.cs` | 内存向量存储、JSON 索引追踪、文件发现 |
| 配置 | `Configuration/`、`appsettings.json` | 强类型 appsettings 选项 |

### 设计原则如何落地

**高可扩展** —— 每个外部依赖都位于接口背后，且在 `Program.cs` 的 DI 中只注册一次。把内存向量库换成
Milvus 客户端、把 ONNX 嵌入换成云端嵌入 API、或换用别的分块器，都只需改一行 DI 注册。工具层与
编排服务从不直接引用具体实现。

**配置仅写在 appsettings** —— 没有命令行参数。`appsettings.json` 控制数据库路径、集合名、嵌入模型与
维度、分块大小/重叠、默认搜索数量、批大小。工具参数（`directory`、`recursive`、`force_reindex`、
`k`）保持原项目的默认值。

**功能一致性** —— 同样三个工具、同样的参数与默认值、同样的增量/全量索引语义、同样的 md5+mtime 变更
检测、同样的以 `---` 分隔的搜索输出、同样的「Already up to date / Directory does not exist」提示，
以及与 Python 原项目一致的服务器名称/版本/指令字符串。

**本地优先** —— 原项目内嵌 Milvus Lite（仅 Python 可用）和 pymilvus 自带的嵌入函数；本项目用本地优先
的默认实现替代（JSON 持久化的内存向量库 + ONNX 嵌入提供者），保留了隐私特性。ONNX 模型在首次运行时
从 Hugging Face 下载并本地缓存，与原项目「首次运行下载模型」的体验一致。

### 配置

所有配置固定在 [`appsettings.json`](appsettings.json) 的 `MarkdownRag` 节：

```jsonc
{
  "MarkdownRag": {
    "DatabasePath": "./.db",                 // 追踪文件与向量库文件所在目录
    "CollectionName": "markdown_vectors",
    "TrackingFileName": "index_tracking.json",
    "VectorStoreFileName": "vectors.json",

    "Embedding": {
      "Provider": "Onnx",                    // 嵌入提供者的 DI 标识
      "Dimension": 768,                      // 必须与所选模型一致
      "ModelId": "sentence-transformers/all-MiniLM-L6-v2",
      "ModelCachePath": "./.models/embeddings",// 首次下载的模型存放目录
      "MaxSequenceLength": 256,
      "PoolingMode": "Mean"
    },
    "Chunking": { "ChunkSize": 512, "ChunkOverlap": 100 },
    "Search":   { "DefaultLimit": 5 },
    "Indexing":  { "BatchSize": 32 }          // 每批嵌入的文档数
  }
}
```

> **首次运行：** 嵌入模型（约 90 MB）会自动从 Hugging Face 下载并缓存到 `ModelCachePath`。为避免
> 首次调用超时，服务器在启动时会开启**后台预加载**：在 MCP 握手进行的同时下载模型并构建 ONNX
> 会话。若工具调用在预加载完成前到达，它会等待加载完成。工具调用还把多个文本**批量**送入一次 ONNX
> 前向推理，使首次调用延迟远低于客户端超时。
>
> **模型放置（手工 / 离线）：** 如果你自己提前下载模型，把两个文件放到下面的缓存目录（不存在则新建）。
> 当两个文件都存在时，服务器会跳过下载。
>
> ```
> .models/embeddings/sentence-transformers/all-MiniLM-L6-v2/
> ├── model.onnx   ← 来自 huggingface.co/.../onnx/model.onnx（约 90 MB）
> └── vocab.txt    ← 来自 huggingface.co/.../vocab.txt
> ```
>
> 路径规则是 `<ModelCachePath>/<ModelId 中的斜杠转为目录分隔符>/`。按上方默认配置、且 exe 从自身目录
> 运行时，即为 `./.models/embeddings/sentence-transformers/all-MiniLM-L6-v2/`。若修改 `ModelId`，
> 子目录名相应改变。

### 安装与运行

发布为可执行文件：

```bash
dotnet publish -c Release -r win-x64 --self-contained false
# 产物：bin/Release/net8.0/win-x64/publish/mcp-markdown-rag.exe
```

（如需不依赖 .NET 运行时的完全自包含构建，使用 `--self-contained true`。）

### 配置 MCP 宿主

#### Claude Code（直接指向编译出的 .exe）

在 Claude Code 中编辑 `.mcp.json`（项目级或用户级），把 `command` 直接指向编译产物，无需
`dotnet run`：

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

> exe 会从自身所在目录读取 `appsettings.json`，因此请把配置好的 `appsettings.json` 放在
> `mcp-markdown-rag.exe` 旁边。Linux/macOS 上使用正斜杠路径指向 `mcp-markdown-rag` 可执行文件。

#### Claude Desktop / Windsurf / Cursor（dotnet run 方式）

```json
{
  "mcpServers": {
    "markdown_rag": {
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "/你的/绝对/路径/markdown_rag_mcp_dotnet",
        "-c",
        "Release"
      ]
    }
  }
}
```

#### 用 MCP inspector 调试

启动 inspector 界面：

```bash
npx @modelcontextprotocol/inspector
```

在 inspector 里打开 **Add Server** 填写字段。本服务是本地 stdio 进程，所以把
**Transport Type** 选为 `STDIO`。其余字段如下：

| 字段 | 填什么 |
|------|--------|
| **Transport Type** | `STDIO` |
| **Command** | 启动服务器的可执行命令（见下方三种方式） |
| **Arguments** | 传给命令的参数，每行一个。直接用 `.exe` 时留空。 |
| **Environment Variables** | 额外环境变量（键值对）。本服务不需要，留空。 |

从下面三种启动方式中选一种：

**A. 直接用 `.exe`（最快，推荐）** —— 需先 `dotnet publish`（见「安装与运行」），并把配置好的
`appsettings.json` 放在 exe 同目录。

- **Command**: `E:\path\to\mcp-markdown-rag.exe`
- **Arguments**: *（留空）*

**B. `dotnet run`（无需先 publish，开发期最方便）**

- **Command**: `dotnet`
- **Arguments**（每行一个）：
  ```
  run
  --project
  E:\path\to\markdown_rag_mcp_dotnet
  -c
  Release
  ```

**C. `dotnet` 指向已构建的 dll**

- **Command**: `dotnet`
- **Arguments**（每行一个）：
  ```
  E:\path\to\markdown_rag_mcp_dotnet\bin\Release\net8.0\mcp-markdown-rag.dll
  ```

点 **Connect**。左下角面板应显示服务器名 `mcp-markdown-rag` 与三个工具
（`index_documents`、`search_documents`、`clear_index`）。

> ⚠️ **首次调用工具可能仍在等待模型加载。** 服务器在 `initialize` 握手后会立即在后台预加载模型
> （约 90 MB）。若你在预加载完成前就调用工具，该调用会等待加载完成——在首次运行时这看起来像卡住。
> 留意 inspector 的日志区（加载完成会打印 `Embedding model preload complete.`）；加载完成后所有调用都会
> 迅速返回，且模型已缓存到磁盘，下次启动无需再下载。

> ⏱️ **为大批量语料调高 Inspector 的请求超时。** MCP Inspector 默认在 **60 000 毫秒（60 秒）**后取消
> 工具调用——在 CPU 上嵌入成千上万个片段可能超过这个时间。要通过 Inspector 索引大目录，必须调高其
> **Request Timeout**：
> - 在 Inspector 界面打开顶部工具栏的 **Settings** 面板，把 **Request Timeout** 设为
>   **600000**（10 分钟 = 60 000 × 10）。`0` 表示「SDK 默认值」（即 60 秒）——要填具体数字，不要填 0。
> - 该值保存在 `~/.mcp-inspector/storage/`（Web 界面则存在浏览器的 `localStorage` 键 `mcp-inspector`），
>   跨会话保留。
> - 服务器无法修改它：这是**客户端**的请求超时，JSON-RPC 服务器无法强制客户端等待更久。索引期间
>   服务器会发送 `notifications/progress` 进度通知（见下文），遵守进度通知的客户端据此重置空闲计时器——
>   但 Inspector 的硬请求超时仍生效，故大批量运行时需调高它。
> - 备选：分批索引较小子目录，或用超时更高 / 无超时的客户端（如经 stdio 的小脚本）调用
>   `index_documents`。

### 直接使用工具

服务器在宿主中注册后，LLM 会替你调用这些工具。你也可以手动驱动它们来做按需索引——例如需要手工对一
批 Markdown 笔记建索引时。最便捷的方式是用 MCP inspector（`npx @modelcontextprotocol/inspector`），
它提供图形界面来逐个调用工具。示例调用：

**索引一个目录（增量、递归）：**

```json
{
  "current_working_directory": "/home/me/notes",
  "directory": "",
  "recursive": true,
  "force_reindex": false
}
```

`current_working_directory` 是基准路径；`directory` 相对于它解析（用 `""` 表示索引基准目录本身）。返回示例：

```json
{
  "message": "Incremental update",
  "processedFiles": 3,
  "totalChunks": 42,
  "files": ["design.md", "roadmap.md", "meeting-notes.md"],
  "mode": "Incremental update"
}
```

**强制全量重建某个子目录：**

```json
{
  "current_working_directory": "/home/me",
  "directory": "projects/wiki",
  "recursive": true,
  "force_reindex": true
}
```

**搜索已索引内容：**

```json
{ "query": "鉴权中间件是如何刷新令牌的？", "k": 5 }
```

**清空索引：**

```json
{}
```

> 首次调用 `index_documents`（或 `search_documents`）会触发一次性的模型下载，因此首次使用会有延迟；
> 之后调用都使用缓存模型。

> ⏱️ **大批量语料与请求超时。** 在 CPU 上嵌入成千上万个片段可能需要几分钟，超过许多客户端的默认
> 请求超时（MCP Inspector 约为 60 秒）。为防止客户端在索引中途取消，当客户端提供 progress token
> 时，`index_documents` 会在每批嵌入完成后发送 `notifications/progress` 进度通知。遵守进度通知的客户端
> （Inspector 在其设置中开启 progress 后即遵守）会在每次更新时重置空闲计时器，使调用保持存活直至完成。
> 若你的客户端未发送 progress token，服务器只是不发送通知照常运行——对超大语料建议分批索引较小子目录，
> 或调高客户端的超时设置。

### 与 Python 原项目的差异

- **向量库** —— 原项目内嵌 Milvus Lite（仅 Python 可用）。本移植使用持久化为 JSON 的内存向量库，
  隐藏在 `IVectorStore` 抽象背后，保留了本地优先的理念。实现该接口即可接入 Milvus。
- **嵌入** —— 原项目使用 pymilvus 自带的 `DefaultEmbeddingFunction`。本移植通过
  `Microsoft.ML.OnnxRuntime` 运行导出为 ONNX 的 Hugging Face sentence-transformer 模型，并做
  mean-pooling 与 L2 归一化（与原项目相同的后处理）。通过 `IEmbeddingProvider` 可替换提供者。
- **分块** —— 原项目使用 LlamaIndex 的 `MarkdownNodeParser` + `TokenTextSplitter`。本移植用托管
  代码复刻了这套两阶段流水线（先按标题切分，再按 token 大小带重叠切分），token 计数使用模型自带的
  BERT 分词器。

### 许可证

Apache 2.0，与上游项目一致。
