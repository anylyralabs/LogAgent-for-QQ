# LogShare 日志解析（内置技能 · 只读）

把小炎接到 [LogShare](https://logshare.cn)（Minecraft/Hytale 日志分享社区）的**只读通道**：
群里有人发 logshare.cn 链接时，小炎可以拉取该日志的**原文**与**结构化解析**，在本地自己分析。

> 定位（柠枺明确）：**LogShare 是为了解析下载用户的日志自己分析，不是市场。**
> 本技能只包含只读工具；**不上传日志、不调用云端 AI**——这两个能力已移除且不得加回。

- API 文档：https://logshare.cn/api-docs
- 服务地址：https://api.logshare.cn（公共 API，无需鉴权）
- 工具（只读）：
  - `logshare_insights` —— 拉取某条分享的结构化解析（GET /v1/insights/{id}）
  - `logshare_raw` —— 下载某条分享的日志原文（GET /v1/raw/{id}）

## 技能清单格式（写新技能的参考）

本目录即一个技能包：`skill.json` 定义元信息 + 工具（HTTP 调用模板），占位符
`{参数名}`（工具参数）与 `{setting.键}`（技能设置）在执行时替换。

```jsonc
{
  "id": "my-skill",            // 唯一 id（目录名建议一致）
  "name": "显示名",
  "type": "http",              // v1 仅支持 http
  "baseUrl": "https://...",
  "settings": [ { "key": "...", "label": "...", "default": "..." } ],
  "tools": [{
    "name": "tool_name",       // 模型看到的工具名
    "description": "...",      // 什么时候用它（对模型行为影响最大）
    "method": "GET",
    "path": "/x/{id}",
    "params": [ { "name": "id", "description": "...", "required": true } ]
  }]
}
```

安装方式：技能商店「从文件安装」（zip / 目录）或直接把技能目录放进用户技能目录后点「刷新」。

- Windows：`%AppData%\LogAgent\skills\`
- Linux / macOS：`~/.config/LogAgent/skills/`（或 `$LOGAGENT_CONFIG_DIR/skills/`）
