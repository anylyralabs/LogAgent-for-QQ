# mclo.gs 日志解析（内置技能 · 只读）

把小炎接到 [mclo.gs](https://mclo.gs)（Minecraft 日志分享社区）的**只读通道**：
群里有人发 mclo.gs 链接时，小炎可以拉取该日志的**原文**与**结构化解析**，在本地自己分析。

> 定位：**只读**——不上传日志、不调用云端 AI，与 LogShare 技能同一红线。

- API 文档：https://mclo.gs/docs/api
- 服务地址：https://api.mclo.gs（公共 API，无需鉴权）
- 工具（只读）：`mclogs_insights`（GET /1/insights/{id}）、`mclogs_raw`（GET /1/raw/{id}）

## 技能清单格式

本目录即一个技能包：`skill.json` 定义元信息 + 工具（HTTP 调用模板），占位符
`{参数名}`（工具参数）与 `{setting.键}`（技能设置）在执行时替换。写法参考 logshare 技能的 README。

安装方式：技能商店「从文件安装」（zip / 目录）或直接把技能目录放进
`%AppData%\LogAgent\skills\` 后点「刷新」。
