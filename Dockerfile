# syntax=docker/dockerfile:1

# ───────────────────────────────────────────────
# Build stage
# ───────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first (layer caching): copy only the project file.
COPY ["McpMarkdownRag.csproj", "McpMarkdownRag.slnx", "./"]
RUN dotnet restore "McpMarkdownRag.csproj"

# Publish the application (tests folder is excluded from the build in the csproj
# and from the docker context via .dockerignore).
COPY . .
RUN dotnet publish "McpMarkdownRag.csproj" \
    -c Release \
    -o /app/publish \
    /p:UseAppHost=false \
    /p:DebugType=None

# ───────────────────────────────────────────────
# Runtime stage
# ───────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

COPY --from=build /app/publish .

# Directories for the index database and the embedding model cache.
# Both are configured through environment variables at runtime (never baked
# into the image) and are meant to be mounted as volumes so a container
# restart does not lose the index or re-download the model:
#   MarkdownRag__DatabasePath=/app/.db
#   MarkdownRag__Embedding__ModelCachePath=/app/.models/embeddings
RUN mkdir -p /app/.db /app/.models/embeddings /data/docs

# The bind URL comes from MarkdownRag__TransportSettings__HttpEndpointUrl
# (UseUrls is applied from appsettings/environment inside the app itself).
EXPOSE 8080

ENTRYPOINT ["dotnet", "mcp-markdown-rag.dll"]
