# Linux 部署指南（无头服务）

> 适用版本：v0.8.0+（含平台适配层与 Headless `serve`）
> 既有 Windows 桌面用法不变，见 [快速上手](03-快速上手.md)。

## 1. 形态说明

| 形态 | 适用 | 入口 |
|---|---|---|
| **无头服务**（推荐） | 服务器 / 群主自己的常驻机 | `LogAgent.Headless serve` |
| systemd 服务 | 开机自启、崩溃重启 | `deploy/logagent.service` |
| Docker | 隔离环境、群主快速部署 | `deploy/Dockerfile` |
| 桌面壳（跨平台） | 有桌面的 Linux / Windows 日常操作 | `LogAgent.Desktop`（Avalonia） |
| 桌面壳（Windows） | 完整体验（含托盘 / 向导 / 全部页面） | `LogAgent.App`（WPF） |

Linux 上没有 WPF 桌面壳：管理通过配置向导的等价流程（编辑 `config.json` + `doctor` 自检）与 CLI 完成。
QQ 接入有两条：**NapCat 反向 WS**（功能最全，含合并转发卡片）或 **QQ 官方 Bot**（合规，无合并转发）。

## 2. 环境要求

| 依赖 | 要求 |
|---|---|
| 操作系统 | Linux x64 / arm64（glibc 2.17+，如 Debian 12、Ubuntu 22.04+、CentOS 9） |
| .NET | 构建需 .NET 10 SDK；发布产物自包含运行时 |
| Shell | `/bin/sh`（运行 `run_shell` 沙箱；可用 `LOGAGENT_SHELL` 指定 bash） |
| QQ 接入 | NapCat（Docker 或本机脚本）或 QQ 官方 Bot 凭证 |
| 可选 | `grep`/`sed`/`awk`/`find`/`python3`（沙箱白名单默认命令，缺哪个模型就用不了哪个） |

## 3. 从源码构建

```bash
git clone https://github.com/anylyralabs/LogAgent-for-QQ.git
cd LogAgent-for-QQ

# 自包含单文件（推荐，目标机不需要装 .NET）
dotnet publish src/LogAgent.Headless -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -o publish/linux-x64

./publish/linux-x64/LogAgent.Headless --help
```

ARM 服务器把 `-r linux-x64` 换成 `-r linux-arm64`。

## 4. 配置

### 4.1 配置位置

| 用途 | 默认路径 |
|---|---|
| 配置目录 | `~/.config/LogAgent/`（XDG；可用 `LOGAGENT_CONFIG_DIR` 覆盖） |
| 配置文件 | `~/.config/LogAgent/config.json` |
| 会话/用量数据库 | `~/.config/LogAgent/data/logagent.db` |
| 知识库 | `~/.config/LogAgent/data/knowledge.db` |
| QQ 文件暂存 | `~/.config/LogAgent/files/`（7 天自动清理） |
| 沙箱工作区 | `~/.config/LogAgent/sandbox/` |

查看实际路径：`LogAgent.Headless config path`。

### 4.2 最小配置

```jsonc
{
  "Providers": [
    {
      "Id": "my-model",
      "Name": "主力模型",
      "Endpoint": "https://<OpenAI 兼容端点>/v1",
      "ApiKey": "<API Key>",
      "ChatModel": "<模型 ID>",
      "Enabled": true
    }
  ],
  "Rules": [
    { "TaskType": "Chat", "ProviderChain": ["my-model"] },
    { "TaskType": "LogAnalysis", "ProviderChain": ["my-model"] },
    { "TaskType": "DeepReasoning", "ProviderChain": ["my-model"] },
    { "TaskType": "Summary", "ProviderChain": ["my-model"] },
    { "TaskType": "Embedding", "ProviderChain": ["my-model"] }
  ],
  "OneBot11": {
    "Enabled": true,
    "Host": "127.0.0.1",
    "Port": 6701,
    "Path": "/onebot/v11/ws",
    "Token": ""
  },
  "AutoStartBots": true
}
```

> 一份 key 同时承担 Chat / LogAnalysis / DeepReasoning / Summary / Embedding；Embedding 可单独指向支持 `/embeddings` 的 provider。

### 4.3 沙箱白名单（Linux 默认）

新建配置时自动按平台生成。Linux 默认：

```
grep sed awk sort uniq wc head tail cut find xargs cat ls stat file echo python3 python
```

> 从 Windows 迁移过来的旧 `config.json` 里白名单可能是 `findstr / type / dir / where` 等 Windows 命令，
> 在 Linux 上会被拒绝执行。启动时会提示：

```
[提示] 沙箱白名单含 Windows 专有命令（findstr, type, dir），在当前平台会被拒绝。
执行 `LogAgent.Headless config repair-sandbox` 可重置为平台默认（会先备份配置）。
```

用 `config repair-sandbox` 显式修复（会把原配置备份成 `config.json.bak-<时间戳>`）。

## 5. NapCat 接入（任选其一）

### 5.1 NapCat 跑在 Docker（推荐）

```bash
docker run -d --name napcat \
  -p 127.0.0.1:6099:6099 -p 127.0.0.1:6701:6701 \
  -v /srv/napcat:/app/napcat \
  mlikiowa/napcat-docker:latest
```

打开 `http://127.0.0.1:6099` 扫码登录 → 网络配置 → 新建**反向 WebSocket Client**：

```
URL:   ws://<日志机 IP>:6701/onebot/v11/ws
Token: 与 config.json 的 OneBot11.Token 一致（留空则两边都空）
```

### 5.2 NapCat 跑在同一台机器（脚本托管）

`config.json` 里填脚本路径即可由服务拉起：

```jsonc
"NapCat": {
  "AutoStart": true,
  "ScriptPath": "/opt/napcat/start.sh",
  "WebUiPort": 6099
}
```

要求脚本有执行位（`chmod +x start.sh`），`.sh` 会以 `/bin/sh` 运行；无扩展名的脚本依赖 shebang。

## 6. 启动与自检

```bash
# 首次：环境自检（配置 / 目录权限 / shell / 白名单 / MCP / 端口）
./publish/linux-x64/LogAgent.Headless doctor

# 端到端自检（内嵌假模型服务，不联网、不写真实配置）
./publish/linux-x64/LogAgent.Headless selftest

# 常驻
./publish/linux-x64/LogAgent.Headless serve
```

`serve` 行为：
- 按 `config.json` 的 `OneBot11.Enabled` / `OfficialBot.Enabled` 自动连接；
- 任一通道启动失败只记录错误，不会让进程退出（其余通道继续可用）；
- 收到 `SIGTERM` / `SIGINT` 时：先切段所有活跃会话（保住 L1 摘要），再断开连接，退出码 0。

## 7. systemd

```bash
sudo useradd -r -s /usr/sbin/nologin logagent || true
sudo mkdir -p /opt/logagent /var/lib/logagent && sudo chown logagent:logagent /var/lib/logagent
sudo cp -r publish/linux-x64/* /opt/logagent/
sudo chmod +x /opt/logagent/LogAgent.Headless

sudo cp deploy/logagent.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now logagent
sudo systemctl status logagent
journalctl -u logagent -f     # 实时日志
```

二进制更新后 `sudo systemctl restart logagent`。

## 8. Docker

```bash
mkdir -p /srv/logagent && cp config.json /srv/logagent/

docker build -f deploy/Dockerfile -t logagent:latest .

docker run -d --name logagent \
  -v /srv/logagent:/config \
  -p 127.0.0.1:6701:6701 \
  --restart unless-stopped \
  logagent:latest

docker logs -f logagent
```

镜像内 `LOGAGENT_CONFIG_DIR=/config`，所有数据都落在挂载卷里。

## 9. 常见问题

**Q1：群里 @ 了没反应？**
`doctor` 看端口与配置；再看 `serve` 日志里是否有 `[OneBot11] WS 服务端启动于 …` 与 `NapCat 已连入`。
最常见的三个原因：NapCat 反向 WS 地址写错、`Token` 两边不一致、白名单是旧的 Windows 命令导致 `run_shell` 全被拒（不影响聊天，只影响工具取证）。

**Q2：`run_shell` 一直提示「不在白名单」？**
Linux 默认白名单不含 `findstr`/`type`/`dir`。检查 `config.json` 里的 `Sandbox.WhitelistCommands`，
或执行 `LogAgent.Headless config repair-sandbox` 重置为平台默认。

**Q3：知识库检索变慢/不命中？**
部分精简镜像的 SQLite 未编译 FTS5，服务会自动降级为 LIKE 检索（日志里有「FTS5 不可用，关键词检索降级为 LIKE」）。
换官方 `mcr.microsoft.com/dotnet/runtime` 镜像即可恢复 FTS。

**Q4：合并转发卡片没有出现？**
合并转发只在 OneBot11（NapCat）通道生效；官方 Bot 通道按普通消息发送。回复需含 ≥2 个【分节】标题。

**Q5：模型端点在内网 / 需要代理？**
`Providers[].Endpoint` 支持任意 OpenAI 兼容地址；网络代理用 systemd `Environment=HTTPS_PROXY=…` 或容器环境变量。

**Q6：数据备份**
备份整个配置目录即可：`tar czf logagent-backup.tgz ~/.config/LogAgent`（密钥只存在 `config.json`，注意保管）。

## 10. 从 Windows 迁移配置

把 `%AppData%\LogAgent\config.json` 拷到 Linux 的 `~/.config/LogAgent/config.json` 即可，但**白名单需要重置**：

```bash
LogAgent.Headless doctor                 # 会提示「沙箱白名单含 Windows 专有命令」
LogAgent.Headless config repair-sandbox  # 备份后重置为 Linux 默认
```

模型、路由、知识库、会话 DB 都是平台无关的，可以直接带过来（SQLite 文件通用）。

## 11. 跨平台桌面壳（可选）

除无头服务外，仓库提供基于 **Avalonia** 的跨平台桌面壳 `src/LogAgent.Desktop`（Windows / Linux / macOS 通用）：

```bash
# 运行（需要桌面会话；Ubuntu/Debian 最小安装可能缺字体与 X11 库）
sudo apt-get install -y libx11-6 libice6 libsm6 libfontconfig1 libfreetype6   # 仅首次
dotnet run --project src/LogAgent.Desktop

# 发布自包含单文件
dotnet publish src/LogAgent.Desktop -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -o publish/desktop-linux-x64
```

**已实现页面**：仪表盘（连接状态 / 模型计量 / 会话与知识库统计）、对话（本机控制台，与 QQ 共用管线）、
模型路由（Provider 列表 + 连通性测试 + 规则链）、运行日志（筛选 / 清屏）、设置（触发规则 / 会话参数 / 沙箱 / 启动项）。

**说明与边界**：
- 它与 WPF 壳（`LogAgent.App`）共用 `LogAgent.Core.AgentHost` 运行时，行为一致；
- 跨平台壳**不包含**：知识库审核、技能商店、会话管理/训练导出、NapCat 扫码托管等页面（这些仍走 Windows 桌面版）；
- 无系统托盘（Linux 下各桌面环境支持度差异大），改为单窗口 + 单实例唤醒；
- 开机自启：Windows 写注册表 `HKCU\Run`，Linux 写 `~/.config/autostart/logagent.desktop`；
- 该壳**不在** `LogAgent.slnx` 内，避免影响 Windows 主构建路径，由 CI 单独构建。

## 12. 与 Windows 的差异

| 能力 | Windows | Linux |
|---|---|---|
| 桌面 GUI | WPF 全功能壳（托盘 / 向导 / 全部页面） | Avalonia 壳（5 个核心页面）；或无头 CLI |
| 沙箱 shell | `cmd.exe`（默认白名单含 findstr/type/dir） | `/bin/sh`（默认白名单含 grep/sed/awk/find） |
| NapCat 托管 | 选择 `.bat`/`.exe` 启动脚本 | 选择 `.sh` 脚本，或直接用 Docker |
| 开机自启 | 注册表 `HKCU\Run` | systemd（或桌面环境的 autostart） |
| 合并转发 | 支持 | 支持（取决于 NapCat 通道） |
