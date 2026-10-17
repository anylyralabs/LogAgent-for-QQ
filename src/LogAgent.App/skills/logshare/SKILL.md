---
name: logshare
description: 解析并下载用户分享到 LogShare 的日志，供小炎在本地分析（只读，不上传）
version: 1.1.0
author: LogAgent 内置
---

# LogShare 日志解析（只读）

当用户发来 logshare.cn 链接、或需要读取某条 LogShare 日志的内容时使用本技能。

## 工具

- `logshare_insights`：获取结构化解析（日志类型、问题列表、版本信息）。
- `logshare_raw`：获取日志原文。

## 注意

- 本技能为**只读**通道：不上传内容到 LogShare、不调用云端 AI 分析——拿到日志后由小炎**自己分析**。
- 链接公开可访问（拿到链接即可查看）：分析时注意提醒用户勿泄露隐私信息（账号、服务器 IP 等）。
