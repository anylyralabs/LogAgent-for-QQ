# mclo.gs 日志解析（内置技能 · 只读）

群里有人发 `mclo.gs/xxxx` 链接时，小炎可以用本技能的只读工具拉取该日志的**原文**与**结构化解析**，在本地自己分析。

> 定位：只读。**不上传日志、不调用云端 AI**——与 LogShare 技能同一红线。

- API 文档：https://mclo.gs/docs/api（公共 API，无需鉴权）
- 工具（只读）：
  - `mclogs_insights` —— 拉取某条分享的结构化解析（GET /1/insights/{id}，JSON）
  - `mclogs_raw` —— 下载某条分享的日志原文（GET /1/raw/{id}）

## 使用提示

- 消息里的 mclo.gs 链接通常会被富化环节**自动抓取**（原文已落盘并在消息里给出「本机路径」）——此时直接用 `read_file`/`search_file` 看本地文件即可，不必再调本技能。
- 本技能用于：按 id 二次查询、富化失败后的手动补救。
