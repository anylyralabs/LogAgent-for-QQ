# LogAgent for QQ —— 无头服务镜像（Linux）
#
# 构建：
#   docker build -f deploy/Dockerfile -t logagent:latest .
# 运行：
#   docker run -d --name logagent \
#     -v /srv/logagent:/config \
#     -p 127.0.0.1:6701:6701 \
#     logagent:latest
# 首次运行前把 config.json 放进挂载目录（见 docs/05-Linux部署.md）。

# ---------- 构建阶段 ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# 先只复制 csproj，利用层缓存还原依赖
COPY src/LogAgent.Shared/LogAgent.Shared.csproj src/LogAgent.Shared/
COPY src/LogAgent.Core/LogAgent.Core.csproj src/LogAgent.Core/
COPY src/LogAgent.Routing/LogAgent.Routing.csproj src/LogAgent.Routing/
COPY src/LogAgent.Memory/LogAgent.Memory.csproj src/LogAgent.Memory/
COPY src/LogAgent.Knowledge/LogAgent.Knowledge.csproj src/LogAgent.Knowledge/
COPY src/LogAgent.Training/LogAgent.Training.csproj src/LogAgent.Training/
COPY src/LogAgent.Connections/LogAgent.Connections.csproj src/LogAgent.Connections/
COPY src/LogAgent.NapCat/LogAgent.NapCat.csproj src/LogAgent.NapCat/
COPY src/LogAgent.Mcp/LogAgent.Mcp.csproj src/LogAgent.Mcp/
COPY src/LogAgent.Sandbox/LogAgent.Sandbox.csproj src/LogAgent.Sandbox/
COPY src/LogAgent.Skills/LogAgent.Skills.csproj src/LogAgent.Skills/
COPY src/LogAgent.Headless/LogAgent.Headless.csproj src/LogAgent.Headless/
RUN dotnet restore src/LogAgent.Headless/LogAgent.Headless.csproj -r linux-x64

COPY src/ src/
RUN dotnet publish src/LogAgent.Headless/LogAgent.Headless.csproj \
    -c Release -r linux-x64 --self-contained true \
    -p:PublishSingleFile=true -p:InvariantGlobalization=false \
    -o /app/publish

# ---------- 运行阶段 ----------
FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app

# 沙箱里常用命令随镜像提供（模型用 run_shell 取证；白名单默认已包含 grep/sed/awk/find 等）
RUN apt-get update && apt-get install -y --no-install-recommends \
        ca-certificates procps \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

ENV LOGAGENT_CONFIG_DIR=/config \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    TZ=Asia/Shanghai

VOLUME /config
EXPOSE 6701

# stop 时发 SIGTERM：应用会切段会话后退出
STOPSIGNAL SIGTERM

ENTRYPOINT ["/app/LogAgent.Headless", "serve"]
