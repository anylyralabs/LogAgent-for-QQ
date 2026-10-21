# QQ 官方 Bot 扫码获取 AppID / AppSecret

一个**独立的小网页工具**：只做「手机 QQ 扫码 → 拿到官方 Bot 凭证」，不进入 LogAgent 主程序、不读写 `config.json`。

## 运行

```bash
dotnet run --project tools/qq-qr-login
# 浏览器打开 http://127.0.0.1:6710/
```

换端口：

```bash
dotnet run --project tools/qq-qr-login -- --port 6711
```

默认只监听 `127.0.0.1`。如果手机/别的电脑要访问（例如工具跑在电脑上、用手机扫码）：

```bash
dotnet run --project tools/qq-qr-login -- --lan
# 或指定监听地址
dotnet run --project tools/qq-qr-login -- --listen 0.0.0.0
```

启动后会打印本机可用的局域网地址，例如 `http://192.168.1.23:6710/`。
**注意**：页面会显示 AppSecret，只应在可信局域网临时使用，用完立即 `Ctrl+C` 关闭。
Windows 首次监听时如弹防火墙，请允许「专用网络」；Linux 需放行端口（如 `ufw allow 6710/tcp`）。

如果跑在远程服务器上，也可以不暴露端口，用 SSH 转发：

```bash
ssh -L 6710:127.0.0.1:6710 user@server
# 然后在本机浏览器打开 http://127.0.0.1:6710/
```

## 流程

1. 页面调用 QQ 开放平台绑定接口创建任务，显示二维码；
2. 手机 QQ 扫码并完成机器人创建/绑定；
3. 页面每 2 秒轮询一次，成功后显示 AppID / AppSecret；
4. 复制到 LogAgent「官方 Bot」页，或写入 `config.json` 的 `Channels[qq-official]`。

二维码过期会自动刷新。AppSecret 只在本页显示，工具不会保存到磁盘，也不写日志。

## 打不开 / 拒绝访问

- **同一台机器**：确认打开的是 `http://127.0.0.1:6710/`，不是 `https://`。
- **手机或别的电脑**：默认只听回环地址，必须加 `--lan`；并用启动日志里的局域网 IP。
- **端口被占用/系统保留**：换端口，例如 `--port 6711`；Windows 的 Hyper-V 保留端口也会导致「拒绝访问」，换一个 1024 以上端口即可。
- **Linux 非 root**：不要用 80/443 等 1024 以下端口。
- **防火墙**：Windows 允许专用网络；Linux 放行对应 TCP 端口。
- **页面出现「轮询失败：Failed to fetch」**：通常是服务进程已退出/被系统冻结，或请求被防火墙拦截。
  - 看启动命令的终端是否还在输出；关掉终端页面就轮询不到了。
  - Android/Termux：切到浏览器前先执行 `termux-wake-lock`，并在系统设置里关闭 Termux 的电池优化。
  - 用 `--verbose` 启动，可看到每个 `/api/status` 请求是否真的到达服务端。
  - 换过端口/重启过服务后，重新打开**新地址**（或硬刷新页面），旧标签页可能还在请求旧端口。

## 说明

- 协议复用 `src/LogAgent.Connections/QrConnectClient.cs`（与 WPF「官方 Bot」页同一条链路）。
- 本项目是辅助工具，**不在** `LogAgent.slnx` 内，也不参与主程序构建/发布。
- 群聊主动推送需要群主在 QQ 群设置中开启「机器人主动在群聊内发言」。
