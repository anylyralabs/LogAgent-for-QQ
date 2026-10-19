# AGENTS.md — LogAgent-for-QQ 编码代理常驻指令

> 本文件面向在此仓库工作的 AI 编码代理。凡与临时对话冲突，以用户当次明确要求为准。

## 1. 项目概况

**LogAgent for QQ（小炎）** —— QQ 群 AI 日志分析助手，面向 Minecraft 玩家与群运维：群里 @ 一下，返回带行号证据的根因报告。

- **技术栈**：C# / .NET 10（LTS），xUnit 2.9，SQLite（`Microsoft.Data.Sqlite`），Avalonia 11（跨平台桌面）
- **两种宿主**：`LogAgent.App`（WPF，Windows 全功能）、`LogAgent.Headless`（无头 CLI/服务，Linux/Windows）
- **双形态**：桌面壳 + 无头服务，共用 `LogAgent.Core.AgentHost` 运行时
- **QQ 接入**：NapCat（OneBot11 反向 WS）或 QQ 官方 Bot，二选一
- **文档**：`docs/` 下 5 篇（规划 / 快速上手 / 分发 / Linux 部署）

## 2. 构建与验证

```bash
dotnet build LogAgent.slnx              # 主解决方案（Windows，含 WPF）
dotnet test tests/LogAgent.Tests/LogAgent.Tests.csproj
```

**Linux / 无 Windows SDK 环境**（只构建跨平台部分）：

```bash
dotnet build src/LogAgent.Headless/LogAgent.Headless.csproj
dotnet build src/LogAgent.Desktop/LogAgent.Desktop.csproj
dotnet build tests/LogAgent.Tests/LogAgent.Tests.csproj
dotnet test  tests/LogAgent.Tests/LogAgent.Tests.csproj
dotnet run --project src/LogAgent.Headless -- selftest    # 端到端自检，不联网
```

**关键约束**：

- `LogAgent.Desktop`（Avalonia）**不在 `LogAgent.slnx` 内**，必须单独构建。这是刻意设计，避免影响 Windows 主构建路径；不要「顺手」把它加进 slnx。
- 本机（Android/Termux）**没有 .NET SDK**，无法本地编译。改代码后只能靠 CI 验证；提交前务必做静态自查（见 §5）。

## 3. 架构要点

### 分层与依赖

```
LogAgent.Shared      配置 / 消息模型 / Prompt / Platform（IShell 等）
LogAgent.Routing     多模型路由（优先链 + 熔断 + 计量）
LogAgent.Memory      会话存储 / 用量持久化（SQLite）
LogAgent.Knowledge   知识库（FTS5 + 向量 + RRF）
LogAgent.Skills      技能注册 + 工具宿主
LogAgent.Sandbox     命令沙箱（白名单 + 审计 + 路径守卫）
LogAgent.Mcp         MCP 客户端（stdio / HTTP）
LogAgent.Core        AgentHost（组合根）+ 各 Agent
LogAgent.Connections OneBot11 / 官方 Bot
LogAgent.NapCat      NapCat 进程托管
LogAgent.Training    训练数据导出
        ↓
LogAgent.App (WPF)  /  LogAgent.Desktop (Avalonia)  /  LogAgent.Headless
```

### 平台差异：只走 `LogAgent.Shared.Platform`

**不要在各模块里写 `OperatingSystem.IsWindows()` 分支**。统一使用：

- `PlatformShell.Current` —— shell 启动方式、默认白/黑名单、提示文案、路径大小写规则
- `PlatformInfo.Current` —— 平台族判定

例外：需要分析器做平台流分析时（如 `File.GetUnixFileMode` 的 CA1416），用 `OperatingSystem.IsWindows()` 守卫。

### 双壳共用运行时

`AgentHost` 是 UI 无关的组合根（会话/沙箱/知识库/技能/MCP/Agent 装配）。
`AppHost`（WPF）只是薄桥接层，公开成员不能随意删改——页面直接绑定这些成员。

## 4. 代码风格

- **注释与文档用中文**；标识符、日志前缀用英文
- file-scoped namespace（`namespace X;`）
- 优先主构造函数（`public sealed class Foo(Dep dep)`）—— 仓库里已有 30+ 处
- 集合用 collection expression（`[]`、`[.. list]`）
- 异常处理：能降级的降级并 `Log(...)`，不要静默吞掉；退出路径可「尽力而为」但需注释说明
- 新增公开 API 必须有 XML 文档注释说明「为什么」，不只是「是什么」

## 5. 提交前自查（无 SDK 时的替代验证）

因本机无法编译，改完代码至少跑一遍静态检查：

```bash
# 1) 括号平衡
# 2) 项目引用是否都存在
# 3) XAML 绑定是否都在对应 VM 里
# 4) using 的命名空间是否都存在
```

**最容易犯的三类错误**（都曾真实发生）：

1. **漏 `using`** —— 例如 `AutoStartManager` 用 `Path`/`File` 却没 `using System.IO`，只有 Windows 构建才暴露
2. **测试里硬编码平台路径** —— 例如 `C:/Windows/win.ini` 在 Linux 上被当作相对路径
3. **交叉编译产物无法执行** —— `arm64` 产物不能在 `x64` runner 上跑（`Exec format error`）

## 6. 测试约定

- 平台相关用例用 `[WindowsOnlyFact]` / `[UnixOnlyFact]` 等属性（见 `PlatformTestAttributes.cs`），不要用 `if (OperatingSystem...) return;` 假装通过
- 测试会改进程级环境变量（如 `LOGAGENT_CONFIG_DIR`）时，必须加 `[Collection("config-dir-env")]` 串行化
- 改动平台层或沙箱默认值时，同步更新 `PlatformTests`

## 7. 安全边界

- **不写密钥**：任何 token / API Key / AppSecret 都不得进入仓库或日志
- **不擅自升级依赖**：`PackageReference` 版本变更需用户确认（尤其 Avalonia 全家桶必须版本一致——DataGrid 最高只到 11.3.13）
- **不擅自改 CI**：工作流的触发条件、job 结构变更要说明理由
- **破坏性操作先确认**：删文件、改数据库 schema、强推分支
- **沙箱默认值变更要谨慎**：Windows 白/黑名单必须与 v0.7.0 逐条一致（有测试守护）

## 8. 交付沟通

- **中文回复，结论先行**
- 引用代码位置带路径与行号，例如 `src/LogAgent.Core/AgentHost.cs:125`
- 不确定就问，不要臆测 API 或行为
- 报告进展时区分「已验证」与「推测」——本机无 SDK，很多结论只能来自 CI

## 9. 已知环境限制

| 项 | 说明 |
|---|---|
| 本地编译 | ❌ 本机无 .NET SDK，验证依赖 CI |
| CI 触发 | `main` 与 `linux` 分支；两个工作流：`ci.yml`（构建+测试）、`build-artifacts.yml`（产物） |
| 产物 | Linux x64/arm64 无头包、Linux 桌面包、Docker 镜像、Windows 桌面 zip |
| 桌面壳 | 只有 5 个页面（仪表盘/对话/模型路由/日志/设置），其余页面仅在 WPF 壳 |

## 10. 常见任务入口

| 任务 | 从哪看起 |
|---|---|
| 改 QQ 消息处理 | `src/LogAgent.Core/AgentRuntime.cs` |
| 改日志分析逻辑 | `src/LogAgent.Core/LogAnalysisAgent.cs` + `LogTools.cs` |
| 改模型路由 | `src/LogAgent.Routing/ModelRouter.cs` |
| 改沙箱/白名单 | `src/LogAgent.Sandbox/` + `LogAgent.Shared/Platform/` |
| 加内置工具 | `src/LogAgent.Skills/FileTools.cs` + `ToolHostHub.cs` |
| 改配置项 | `src/LogAgent.Shared/Configuration/` |
| 加桌面页面 | `src/LogAgent.Desktop/Views/` + `ViewModels/`（记得在 `App.axaml` 注册 DataTemplate） |
