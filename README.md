# LogAgent for QQ（小炎）

QQ 群 AI 日志分析助手——把「贴日志 → 等人肉排障」变成「@ 一下 → 拿到带证据的根因报告」。

以 Minecraft / 启动器 / 渲染器崩溃排障为主要场景，也可用于通用群聊问答。背后是一个带工具调用、知识库与学习记忆的多 Agent 系统，UI 为 WPF 托盘应用。

## 功能特性

- **双通道 QQ 接入**：NapCat（OneBot 11 反向 WS）+ QQ 官方 Bot，托盘常驻、QQ 式收起/唤醒
- **日志理解工具链**：群文件 / LogShare 链接自动完整落盘（不省略中间），模型用 `read_file`（按行精读）、`search_file`（正则定位）、`run_shell`（白名单沙箱 + 审计）自行取证后给出**精确到行号的根因报告**
- **Plan-Execute + Reflection 分析**：根因分析带自检评审，不可靠结论自动附风险提示
- **合并转发卡片**：长报告按【结论】/【原因分析】/【解决方法】/【相关建议】分节渲染为一张转发卡片，不再刷屏；失败自动回退普通发送
- **知识库 + 学习记忆**：RAG 检索命中带引用编号回答；对话沉淀候选知识，审核后复用；会话自动切段摘要
- **多模态**：群里发崩溃截图，直接看图定位问题
- **技能系统 + MCP**：SKILL.md 指令型技能（渐进式披露）+ HTTP 工具技能 + 标准 MCP 服务器接入
- **命令沙箱**：白名单/黑名单 + 超时杀进程树 + 全量审计
- **用量统计**：按日 × Provider × 模型 × 任务持久化聚合，盯免费额度一目了然
- **多模型路由**：Provider 优先链 + 故障切换 + 熔断，OpenAI 兼容协议

## 快速上手

```bash
# 构建
dotnet build LogAgent.slnx

# 运行测试（200+ 用例）
dotnet test tests/LogAgent.Tests/LogAgent.Tests.csproj

# 发布单文件
dotnet publish src/LogAgent.App/LogAgent.App.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

1. 启动 `LogAgent.App.exe`，首次启动进入配置向导（配置模型 Provider、QQ 接入方式）
2. 模型通道：任意 OpenAI 兼容端点（本地代理、聚合 API、自建网关均可）
3. QQ 接入二选一：
   - **NapCat**：安装 [NapCat](https://github.com/NapNeko/NapCatQQ)，反向 WS 指向本应用（默认 `ws://127.0.0.1:6701/onebot/v11/ws`）
   - **官方 Bot**：QQ 开放平台创建机器人，填入 AppID / AppSecret
4. 群里 @ 机器人发日志链接/文件，或直接问问题

> ⚠️ 免责声明：本项目仅供学习研究。通过 NapCat 等非官方协议接入 QQ 存在账号风险，请自行评估；产生的回复内容不代表任何官方立场。

## 架构一览

```
LogAgent.App          WPF 主程序（托盘、向导、页面）
LogAgent.Core         AgentRuntime / GroupContextAgent / LogAnalysisAgent（Plan-Execute + Reflection）
LogAgent.Connections  OneBot11Connection / OfficialBotConnection / QrConnect
LogAgent.Routing      多模型路由（优先链 + 熔断 + 计量）
LogAgent.Knowledge    知识库（RAG + 审核流）
LogAgent.Memory       会话存储 / 学习提炼 / 模型用量持久化
LogAgent.Skills       技能注册（SKILL.md + HTTP 工具）+ 内置工具宿主（read_file / search_file / run_shell）
LogAgent.Sandbox      命令沙箱（白名单 + 审计 + 路径守卫）
LogAgent.Shared       协议消息模型 / 配置 / 提示词集中管理
LogAgent.Training     训练数据导出
```

## 文档

- [Agent 应用构建工作流笔记](docs/01-Agent应用构建工作流-学习笔记.md)
- [日志分析 Agent 应用规划](docs/02-日志分析Agent应用规划.md)
- [快速上手](docs/03-快速上手.md)
- [分发与首启体验](docs/04-分发与首启体验.md)

`tools/` 下有部署、测试与运维脚本（ NapCat 文件拉取、E2E 生成器、UI 自动化截图等）。

## License

[MIT](LICENSE)
