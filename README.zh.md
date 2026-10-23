# MCP-Markdown-RAG (.NET 10)

一个提供**本地优先 RAG 引擎**的 **Model Context Protocol (MCP)** 服务器，用于你的 markdown
文档：它把 `.md` 文件索引进本地向量数据库，并对其执行语义搜索。本项目是
[MCP-Markdown-RAG](https://github.com/Zackriya-Solutions/MCP-Markdown-RAG) 的忠实 .NET 10
移植版——保留原版的全部行为与工具面，同时采用干净的、高可扩展的 .NET 设计。

✅ **本地优先 & 隐私** — 所有数据处理与存储都在本地完成，索引过程不向任何远端发送数据
（仅首次运行需下载嵌入模型）。

✅ **markdown 语义搜索** — 按概念含义检索文档段落，而不是简单的关键词匹配。

✅ **兼容 MCP** — 可接入任何 MCP 宿主（Claude Desktop、Claude Code、Cursor、Windsurf、
MCP inspector 等）。

✅ **双传输层** — 既可作为 stdio 子进程运行（默认），也可作为基于 Kestrel 的 Streamable
HTTP 服务运行，通过 `appsettings.json` 一键切换。

✅ **高可扩展** — 每个外部关注点（嵌入、向量存储、分块、文件发现、索引跟踪）都位于接口
之后，可经 DI 替换为其他实现，无需改动调用方。

✅ **Docker & docker compose 就绪** — 一个镜像、环境变量配置、卷挂载的索引数据库与模型
缓存（见 [Docker 部署](#docker-部署)）。

✅ **对外暴露的 HTTP 面** — `Transport=Http` 时，同一条 Kestrel 管线同时承载 MCP
Streamable HTTP 端点、Swagger UI（OpenAPI 文档）、MCP 发现面的只读 REST 孪生
（`/api/tools`）、面向浏览器宿主前端的 CORS，以及公开的发现/健康端点。

## 工具

服务器暴露三个工具，语义与原版一致：

| 工具 | 描述 | 参数 |
|------|------|------|
| `index_documents` | 将 markdown 文件索引进本地向量数据库。默认增量更新；`force_reindex=true` 全量重建。大语料分批嵌入并推送进度通知。 | `current_working_directory`（必填）、`directory`（默认 `""`）、`recursive`（默认 `false`）、`force_reindex`（默认 `false`） |
| `search_documents` | 对已索引分块做语义搜索。按 `---` 分隔符拼接返回前 k 个命中，无命中时返回 `"No results found."`。 | `query`（必填）、`k`（默认 `5`） |
| `clear_index` | 丢弃向量集合并重置跟踪文件。 | — |

索引模式与原版一致：

- **全量重建**（`force_reindex=true`）— 丢弃并重建整个索引。
- **增量更新**（默认）— 经由 md5 哈希 + 修改时间的跟踪文件检测变更文件，清除其旧分块后
  只重新嵌入变更文件。
- **递归索引**（`recursive=true`）— 下钻子目录，跳过以点号开头的目录（`.git`、`.obsidian`
  等）。

服务器返回与 Python 原版完全相同的消息字符串：`"Full reindex"`、`"Incremental update"`、
`"Already up to date, Nothing to index!"`、`"Directory does not exist!"`，以及
`---` 分隔的搜索输出。

## 快速开始

### 1. 配置

全部配置集中管理于 [`appsettings.json`](src/McpMarkdownRag/appsettings.json) 的 `MarkdownRag` 节——不提供
命令行配置面：

```jsonc
{
  // 可选的 Bearer 令牌，保护每一个 HTTP 请求（/mcp 端点）。
  // 非空 => 请求必须携带 "Authorization: Bearer <value>"。
  // 环境变量覆盖："server-token"（或 "ServerToken"）。留空则关闭鉴权。
  "server-token": "",

  "MarkdownRag": {
    "Transport": "Stdio",                        // "Stdio"（默认）或 "Http"（Streamable HTTP）
    "TransportSettings": {
      "HttpEndpointUrl": "http://localhost:3001", // Kestrel 绑定地址（仅 Transport=Http）
      "HttpRoutePrefix": "",                     // 路由前缀，例如 "/mcp"；"" 表示根路径
      "Stateless": true                           // 无状态模式（服务器推荐开启）
    },
    "DatabasePath": "./.db",                      // 跟踪文件 + 向量存储文件
    "CollectionName": "markdown_vectors",
    "TrackingFileName": "index_tracking.json",
    "VectorStoreFileName": "vectors.json",
    "Embedding": {
      "Provider": "Onnx",                          // DI 中嵌入提供者的键
      "Dimension": 384,                            // 必须与所配置模型一致
      "ModelId": "sentence-transformers/all-MiniLM-L6-v2",
      "ModelCachePath": "./.models/embeddings",    // 首次运行下载落盘处
      "MaxSequenceLength": 256,
      "PoolingMode": "Mean"
    },
    "Chunking": { "ChunkSize": 512, "ChunkOverlap": 100 },
    "Search":   { "DefaultLimit": 5 },
    "Indexing":  { "BatchSize": 32 }               // 每批嵌入的文档数
  }
}
```

> **环境变量** — 所有配置值均可使用标准 `__` 分段记法覆盖（Docker 场景尤为方便）：
>
> ```bash
> MarkdownRag__Transport="Http"
> MarkdownRag__TransportSettings__HttpEndpointUrl="http://0.0.0.0:8080"
> MarkdownRag__TransportSettings__HttpRoutePrefix="mcp"
> MarkdownRag__TransportSettings__Stateless="true"
> MarkdownRag__DatabasePath="/app/.db"
> MarkdownRag__Embedding__ModelCachePath="/app/.models/embeddings"
> ServerToken="my-secret"        # 根级 "server-token" 的 Windows 友好别名
> ```

> **首次运行** — 嵌入模型（`model.onnx` + `vocab.txt`，约 90 MB）会自动从 Hugging Face
> 下载并缓存到 `ModelCachePath`。启动后还有一个**后台预加载**托管服务负责下载模型并构建
> ONNX 会话，避免首次工具调用承担该成本；若工具调用先于预加载完成，它只会等待加载完成。
> 若要改用手动下载的模型，把两个文件放入缓存目录即可：
>
> ```
> .models/embeddings/sentence-transformers/all-MiniLM-L6-v2/
> ├── model.onnx
> └── vocab.txt
> ```

> **传输层选择** —
> - `"Stdio"`（默认）— 经 stdin/stdout 的 JSON-RPC；服务器作为 MCP 宿主（Claude Desktop、
>   Cursor、Claude Code 等）的子进程运行。日志被强制重定向到 stderr，保证协议流不被污染。
> - `"Http"` — 基于 Kestrel 的 Streamable HTTP，监听 `HttpEndpointUrl`，挂载于
>   `HttpRoutePrefix`（如 `/mcp`）。推荐 `Stateless=true`；仅当客户端需要服务器主动发起
>   请求（sampling/elicitation）时才应关闭——本服务器不会发起这类请求。`Transport=Stdio`
>   时 `TransportSettings` 下任何设置都会被忽略。

### 2. 运行

```bash
dotnet run --project src/McpMarkdownRag   # 默认 stdio 服务器
# 或
dotnet publish src/McpMarkdownRag -c Release -r win-x64 --self-contained false
# exe: bin/Release/net10.0/win-x64/publish/mcp-markdown-rag.exe
```

exe 会从自身所在目录解析 `appsettings.json`，因此请把配置副本放在二进制同目录。

也支持 launch profile，且传输层决策感知环境：

```bash
dotnet run -lp hopcos                      # 由 profile 设置 ASPNETCORE_ENVIRONMENT=hopcos
```

profile 的 `ASPNETCORE_ENVIRONMENT` 会选中环境专属的 `appsettings.<环境>.json`
（例如 `appsettings.hopcos.json` 配置了 `Transport=Http`），因此该命令会启动对外暴露的
HTTP 面——Kestrel 会打印绑定 URL（"Now listening on: http://localhost:3001"）。
注意优先级：`ASPNETCORE_ENVIRONMENT`（launch profile 设置的变量）优先于
`DOTNET_ENVIRONMENT`，且环境变量覆盖 JSON 文件，所以 `MarkdownRag__Transport=Stdio`
可以切回 stdio。

### 3. 注册到 MCP 宿主

#### Claude Code（stdio，指向构建出来的 exe）

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

Linux/macOS 上使用指向二进制的正斜杠路径。

#### Claude Desktop / Cursor（经 dotnet run 的 stdio）

```json
{
  "mcpServers": {
    "markdown_rag": {
      "command": "dotnet",
      "args": ["run", "--project", "/绝对路径/Markdown-RAG-MCP-Dotnet/src/McpMarkdownRag", "-c", "Release"]
    }
  }
}
```

#### HTTP 宿主（Streamable HTTP 传输）

设 `Transport=Http`，把支持 HTTP 的 MCP 宿主指向端点，例如 Claude Desktop：

```json
{
  "mcpServers": {
    "markdown_rag": {
      "url": "http://localhost:3001/mcp"
    }
  }
}
```

若服务器配置了令牌（见[鉴权](#鉴权)），HTTP 宿主经请求头携带令牌，例如 Claude Code：

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

### 鉴权

当根级 `server-token` 配置值**非空**时，每一个 HTTP 请求（CORS 预检与刻意公开的路径
除外——`/swagger` 下的 Swagger UI 与其 JSON 文档、根发现端点 `/`、健康检查 `/health`）
都必须携带令牌；留空则完全关闭鉴权：

```bash
curl -H "Authorization: Bearer my-secret" http://localhost:3001/mcp
```

令牌凭据接受 `Authorization: Bearer <token>` 请求头，或面向浏览器发起的 GET 请求的
`?access_token=<token>` 查询参数。缺失或错误的凭据返回 `401 Unauthorized`，附
`WWW-Authenticate: Bearer` 与载荷 `{"error":"UNAUTHORIZED","message":"Invalid authorization: ..."}`。
令牌比较为恒时操作（SHA-256 摘要，无时序侧信道）。

> 配置位于 appsettings 根级：`"server-token": ""`。可用环境变量 `server-token`
> （Docker/Linux）或 `ServerToken`（Windows 宿主——环境变量名不允许包含连字符）覆盖。

> **安全。** 默认 `HttpEndpointUrl` 绑定 `localhost`，服务器仅本机可达。若要绑定
> `0.0.0.0`（或容器端口）对外共享，请启用内置 Bearer 令牌门（[鉴权](#鉴权)）和/或放在强制
> TLS 的反向代理之后。stdio 传输（MCP 宿主注册的可信子进程）不经过 HTTP 管道，因此不受
> 令牌门影响。

### HTTP Web 面

`Transport=Http` 时，同一条 Kestrel 管线同时承载 MCP 端点与对外暴露的 REST 面：

| 路径 | 鉴权 | 用途 |
|------|------|------|
| `/mcp`（前缀可配置） | `server-token` 非空时必填 | MCP Streamable HTTP 端点（initialize、tools/list、工具调用） |
| `/` | 公开 | 服务器发现信息：名称、传输层、MCP 路径、无状态模式、工具名与各面链接 |
| `/health` | 公开 | 健康检查——返回 `{"status":"ok"}` |
| `/swagger` | 公开 | 面向 OpenAPI 文档（`/swagger/v1/swagger.json`）的 Swagger UI；Authorize 对话框为鉴权调用提供 `Bearer <server-token>` |
| `/api/tools` | `server-token` 非空时必填 | MCP 发现面的只读 REST 孪生：三个工具及描述与类型化参数行，与 `tools/list` 同源的 `[McpServerTool]` 标注实时解析 |

浏览器宿主的前端经 CORS 集成（`AllowAnyOrigin/Method/Header`）。未处理异常按统一的
错误契约回答 `{"error":"<code>","message":"..."}`：畸形请求 → `400 BAD_REQUEST`、
超时 → `504 TIMEOUT`、其余 → `500 INTERNAL_ERROR`。

### 4. 用 MCP inspector 调试

```bash
npx @modelcontextprotocol/inspector
```

添加服务器，选择对应的 **Transport Type**（stdio：command + args；或 **STREAMABLE
HTTP**：URL）后连接。服务器上报 `mcp-markdown-rag`，含上述三个工具。

> ⏱️ **大语料与客户端超时。** CPU 上嵌入数千个分块可能超过客户端默认请求超时
> （inspector 60 秒后取消）。对大型索引任务，请在 inspector 的 **Request Timeout**
> （Settings 面板）调大该超时——服务器在每批嵌入完成后会推送
> `notifications/progress`，支持进度通知的客户端会重置空闲计时器。

### 5. 只携带向量索引部署到纯查询服务器

查询永远不读取原始 markdown 文件：持久化的每条向量记录都把分块**原文**和源**文件名**
内嵌在 `vectors.json` 里，`search_documents` 完全只从向量库作答。查询机上既不需要
`.md` 源文件，也不需要增量跟踪文件（`index_tracking.json`）。

```bash
# 1. 照常在本地索引。
dotnet run -lp hopcos   # http://localhost:3001 → index_documents

# 2. 只把持久化索引复制到远程服务器（保持相同相对路径）：
#    <DatabasePath>/vectors.json   （默认：./.db/vectors.json）

# 3. 在远程启动服务器——立即可查询。
dotnet run -lp hopcos
```

要拷贝的 | 不需要拷贝的
---|---
`vectors.json`（整个 `DatabasePath` 目录一并拷贝也可以） | 源 `.md` 文件、`index_tracking.json`

远程机器仍需要查询语义所依赖的东西：**嵌入模型缓存**（`.models/`，必须与索引时使用
的是同一个模型——查询文本要靠它做嵌入）以及 `Transport=Http` 的服务配置。**不要在纯
查询服务器上调用 `index_documents`**：没有源文件时它会返回 `Directory does not exist!`，
增量更新能力只存在于源文件所在的完整部署中。

### 工具调用示例

**索引一个目录（增量、递归）：**

```json
{
  "current_working_directory": "/home/me/notes",
  "directory": "",
  "recursive": true,
  "force_reindex": false
}
```

**全量重建一个子目录：**

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
{ "query": "How does the auth middleware refresh tokens?", "k": 5 }
```

**清空索引：**

```json
{ }
```

## Docker 部署

构建镜像后可手动运行或经 compose 运行。容器内运行 **HTTP** 传输；索引数据库与模型缓存
均由卷挂载，容器重建不丢失。

### docker compose（推荐）

```bash
docker compose up -d --build
```

`docker-compose.yml` 全部经环境变量配置：

| 变量 | 值 | 用途 |
|------|----|------|
| `MarkdownRag__Transport` | `Http` | 容器内启用 Streamable HTTP |
| `MarkdownRag__TransportSettings__HttpEndpointUrl` | `http://0.0.0.0:8080` | 绑定所有网卡 |
| `MarkdownRag__TransportSettings__HttpRoutePrefix` | `mcp` | MCP 端点位于 `/mcp` |
| `MarkdownRag__TransportSettings__Stateless` | `true` | HTTP 请求相互独立 |
| `MarkdownRag__DatabasePath` | `/app/.db` | 卷挂载的索引数据库 |
| `MarkdownRag__Embedding__ModelCachePath` | `/app/.models/embeddings` | 卷挂载的模型缓存 |
| `server-token` | `${MCP_SERVER_TOKEN:-}` | 保护每一个 `/mcp` 请求的可选 Bearer 令牌（留空关闭鉴权） |

卷（在 compose 项目目录下创建）：

- `./db` → `/app/.db` — `index_tracking.json` 与 `vectors.json` 在重启后保留。
- `./hf-models` → `/app/.models/embeddings` — ONNX 模型只下载一次，卷避免容器重建时重复
  下载。
- `./docs` → `/data/docs`（可选，默认注释掉）— 挂载你的 markdown 语料后可用
  `current_working_directory=/data/docs`（+ `directory=<子目录>`）索引。

部署后的 MCP 端点：`http://<host>:8080/mcp`。配置了令牌时，可用
`curl -H "Authorization: Bearer $MCP_SERVER_TOKEN" http://localhost:8080/mcp` 验证。

### 手动 docker 构建

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

> Dockerfile 以 `/p:UseAppHost=false /p:DebugType=None` 发布（dll 入口、不含 PDB），运行
> 层基于 `mcr.microsoft.com/dotnet/aspnet:10.0`，与基于 Kestrel 的 Streamable HTTP 传输
> 匹配。`.dockerignore` 排除 `bin/`、`obj/`、`tests/`、`.db/`、`.models/` 与文档文件，既
> 保持构建上下文精简，也避免镜像携带既有索引或模型。

## 测试

测试套件启动真实应用管道，覆盖完整链路——单元层（分块器、存储、编排）与集成层（经
TestServer 的真实 `/mcp` 端点）：

| 层级 | 文件 | 断言内容 |
|------|------|----------|
| 单元 | `tests/McpMarkdownRag.Tests/Unit/MarkdownChunkerTests.cs` | 标题切分 + 带重叠的按令牌二次切分、空输入处理 |
| 单元 | `tests/McpMarkdownRag.Tests/Unit/StorageTests.cs` | md5+mtime 指纹、JSON 跟踪持久化、文件发现（递归模式跳过点号目录）、向量存储插入/搜索/删除/丢弃 |
| 单元 | `tests/McpMarkdownRag.Tests/Unit/MarkdownRagServiceTests.cs` | 用确定性假嵌入提供者 + 内存 mock 文件系统跑全生命周期：不存在目录的消息、增量→无事可做、搜索输出、全量重建、清空 |
| 单元 | `tests/McpMarkdownRag.Tests/Security/BearerTokenMiddlewareTests.cs` | `server-token` 为空/缺失时鉴权关闭；接受 Bearer 头与 `?access_token=`；错误/畸形/缺失凭据 → 401 契约载荷；CORS 预检放行；公开路径豁免（Swagger UI/文档、`/`、`/health`）无凭据可直达；形近路径仍受保护；恒时比较 |
| 集成 | `tests/McpMarkdownRag.Tests/Integration/McpIntegrationTests.cs` | 真实 `/mcp` Streamable HTTP 契约：`initialize`（serverInfo + instructions）、`tools/list`（三个工具）、完整的 `index_documents` → `search_documents` → `clear_index` 流程（含原版消息字符串），外加 DI 装配健全性检查 |
| 集成 | `tests/McpMarkdownRag.Tests/Integration/AuthTokenIntegrationTests.cs` | 开启鉴权的服务器夹具：无凭据与错误令牌的 MCP 请求 → 401 契约载荷；有效 Bearer 令牌完成 MCP 会话初始化并到达 `tools/list`；`/api/tools` 孪生无凭据 → 401，而 `/`、`/health` 与 `/swagger` 保持公开 |
| 集成 | `tests/McpMarkdownRag.Tests/Integration/WebSurfaceIntegrationTests.cs` | 公开 HTTP 面端到端：根信息 + `/health` 载荷、描述 `/api/tools` 的公开 Swagger 文档、实时解析的 `/api/tools` 目录（三个工具、类型化参数行的必填/可选），以及感知环境的传输层决策（`ASPNETCORE_ENVIRONMENT=hopcos` 且不设 Transport 环境变量 → `appsettings.hopcos.json` 的 `Transport=Http` 启动 HTTP 面） |
| 集成 | `tests/McpMarkdownRag.Tests/Integration/PortableIndexIntegrationTests.cs` | 索引可移植性：索引后**只**把 `vectors.json` 复制到全新服务器目录（无 markdown 源、无 `index_tracking.json`），`search_documents` 即可返回文件名与完整分块原文 |

集成夹具把 ONNX 管道替换为确定性 `FakeEmbeddingProvider`（BERT 分词器同理替换为轻量
实现），测试全程无模型下载、无网络访问。一键运行全部测试：

```bash
dotnet test tests/McpMarkdownRag.Tests
```

代码覆盖率收集经 `coverlet.collector` 接线：

```bash
dotnet test tests/McpMarkdownRag.Tests --collect "XPlat,Code Coverage"
```

## 项目结构

| 区域 | 文件 | 职责 |
|------|------|------|
| 宿主 | `src/McpMarkdownRag/Program.cs` | 宿主 + DI 装配；stdio *或* Streamable HTTP 传输、日志重定向到 stderr |
| 工具 | `src/McpMarkdownRag/Tools/MarkdownRagTools.cs` | 轻薄的 MCP 工具适配器（`[McpServerTool]` 方法） |
| 编排 | `src/McpMarkdownRag/Services/MarkdownRagService.cs` | 索引/搜索编排 |
| 预加载 | `src/McpMarkdownRag/Services/EmbeddingPreloadHostedService.cs` | 启动时后台模型预加载 |
| 抽象 | `src/McpMarkdownRag/Abstractions/*.cs` | `IEmbeddingProvider`、`IVectorStore`、`IMarkdownChunker`、`IIndexTracker`、`IMarkdownFileDiscovery`、`ITextTokenizer` |
| 分块 | `src/McpMarkdownRag/Chunking/MarkdownChunker.cs` | 标题切分 → 令牌切分（512 / 100 重叠） |
| 嵌入 | `src/McpMarkdownRag/Embeddings/*.cs` | ONNX 嵌入提供者 + BERT 分词器 + 模型下载器 |
| 存储 | `src/McpMarkdownRag/Storage/*.cs` | 内存向量存储、JSON 索引跟踪、文件发现 |
| 安全 | `src/McpMarkdownRag/Security/BearerTokenMiddleware.cs` | 面向每个 HTTP 请求的可选 `server-token` Bearer 门（含公开路径豁免） |
| API | `src/McpMarkdownRag/Api/MarkdownRagApiEndpoints.cs` | MCP 发现面的只读 REST 孪生（`/api/tools`） |
| 配置 | `src/McpMarkdownRag/Configuration/`、`src/McpMarkdownRag/appsettings.json` | 强类型 appsettings 选项 |
| 测试 | `tests/McpMarkdownRag.Tests/` | 单元 + 全链路集成测试（TestServer） |
| Docker | `Dockerfile`、`docker-compose.yml`、`.dockerignore` | 容器部署 |

## 设计原则如何落地

**高扩展性** — 每个外部依赖都位于接口之后，并在 `Program.cs`（DI）中注册且仅注册一次。
把内存存储换成 Milvus 客户端、把 ONNX 嵌入换成云端嵌入 API、把分块器换成替代实现，成本
只是改动一条 DI 注册；工具与编排服务从不引用具体实现。

**配置仅集中在 appsettings** — 不提供命令行配置面。`appsettings.json` 控制传输层
（stdio vs. HTTP）、HTTP 绑定地址/路由、数据库路径、集合名、嵌入模型 + 维度、分块大小/
重叠、默认搜索条数与批大小。工具参数（`directory`、`recursive`、`force_reindex`、`k`）
保留原版默认值。

**功能对等** — 相同的三个工具、相同的参数与默认值、相同的增量 vs 全量重建语义、相同的
md5+mtime 变更检测、相同的 `---` 分隔搜索输出、相同的消息字符串，以及与 Python 原版一致
的服务器名称/版本/instructions 字符串。

**本地优先** — 原版内嵌 Milvus Lite（仅 Python）与 pymilvus 自带的嵌入函数；本移植以
本地优先的默认实现（JSON 持久化的内存向量存储、ONNX 嵌入提供者）替代，隐私保证不变。
ONNX 模型首次运行经 Hugging Face 下载并本地缓存，与原版"首次运行下载模型"的体验一致。

## 与 Python 原版的差异

- **向量存储** — 原版内嵌 Milvus Lite（仅 Python）。本移植使用 JSON 持久化的内存向量
  存储（`IVectorStore`）；实现该接口即可接入 Milvus 或任何向量库。
- **嵌入** — 原版使用 pymilvus 自带的 `DefaultEmbeddingFunction`。本移植经
  `Microsoft.ML.OnnxRuntime` 运行导出为 ONNX 的 Hugging Face 句向量模型（默认 CPU
  provider；可经 DI 换入 CUDA/DirectML 扩展，无需改动调用方），带均值池化 + L2 归一化
  ——与原版后处理一致。经 `IEmbeddingProvider` 可替换。
- **分块** — 原版用 LlamaIndex 的 `MarkdownNodeParser` + `TokenTextSplitter`。本移植在
  托管代码中复刻两段式管道（先按标题切分，再按令牌大小带重叠二次切分），令牌计数使用
  模型的 BERT 分词器。
- **点号目录跳过由服务器自身实现** — 递归枚举自行过滤以点号开头的目录段
  （`EnumerationOptions` 不含点号目录开关），在 Windows、Linux、macOS 上均保证与原版
  行为一致。

## 许可证

Apache 2.0，与上游项目一致。

---


[English version](README.md)
