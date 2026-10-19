using System.Collections.Concurrent;
using LogAgent.Connections;
using LogAgent.Core;
using LogAgent.Memory;
using LogAgent.Routing;
using LogAgent.Shared.Configuration;
using LogAgent.Shared.Messaging;

namespace LogAgent.App.Services;

/// <summary>
/// 应用级单例：跨页面共享的连接与运行时装配（页面导航会重建 Page，状态必须放这里）。
/// OneBot11 连接 + AgentRuntime + 路由器的组合根。
/// 与 UI 无关的「运行时装配」已下沉到 <see cref="AgentHost"/>（Headless 复用同一份），这里只保留连接与日志桥接。
/// </summary>
public static class AppHost
{
    private static OneBot11Connection? _oneBot;
    private static AgentHost? _host;
    private static readonly CoreBridge Bridge = new();

    /// <summary>运行时组合根（懒加载；构造时读配置并接好计量事件）。</summary>
    private static AgentHost Host => _host ??= CreateHost();

    private static AgentHost CreateHost()
    {
        var host = new AgentHost(AppConfigStore.Load());
        host.Logged += OnLog;
        host.LocalChatReply += reply => LocalChatReply?.Invoke(reply);
        return host;
    }

    public static AppConfig Config { get; private set; } = AppConfigStore.Load();

    public static ModelRouter Router => Host.Router;

    /// <summary>模型用量持久化（按日聚合，跨重启累计；仪表盘「模型调用」与今日累计数据源）。</summary>
    public static ModelUsageStore Usage => Host.Usage;

    /// <summary>路由计量统计（仪表盘看板）。</summary>
    public static RouterStats Stats => Host.Stats;

    public static SessionManager? Sessions => Host.Sessions;

    public static Sandbox.SandboxService? Sandbox => Host.Sandbox;

    public static Knowledge.KnowledgeStore? Knowledge => Host.Knowledge;

    public static Skills.SkillRegistry? Skills => Host.Skills;

    /// <summary>MCP 服务器管理器（标准协议客户端）。</summary>
    public static LogAgent.Mcp.McpManager? Mcp => Host.Mcp;

    private static AgentRuntime? Runtime => Host.Runtime;

    public static bool OneBotServerStarted { get; private set; }

    /// <summary>官方 Bot（QQ 开放平台）连接实例。</summary>
    public static OfficialBotConnection? OfficialBot { get; private set; }

    public static bool OfficialBotRunning => OfficialBot?.IsConnected ?? false;

    /// <summary>已发起连接但 WS 尚未 open（连接中状态）。</summary>
    public static bool OfficialBotPending => OfficialBot is not null && !OfficialBot.IsConnected;

    /// <summary>启动官方 Bot 连接（凭证从最新配置读取，独立于 OneBot11 服务）。</summary>
    public static async Task<string> StartOfficialBotAsync()
    {
        if (OfficialBot is not null) return "官方 Bot 已在运行";

        var config = AppConfigStore.Load().OfficialBot;
        if (string.IsNullOrWhiteSpace(config.AppId) || string.IsNullOrWhiteSpace(config.AppSecret))
            return "请先填写并保存 AppID / AppSecret";

        var bot = new OfficialBotConnection(config);
        bot.ConnectionLog += (_, line) => OnLog(line); // 连接内部日志已带 [官方Bot] 前缀
        bot.MessageReceived += async (_, e) =>
        {
            try
            {
                Host.Ensure();
                if (Runtime is not null)
                    await Runtime.OnMessageAsync(Bridge, e.Message);
            }
            catch (Exception ex)
            {
                OnLog($"[官方Bot] 处理消息失败：{ex.Message}");
            }
        };
        Host.Ensure();
        await bot.StartAsync();
        OfficialBot = bot;
        OnLog($"[官方Bot] 已启动：{bot.Name}");
        return "已连接";
    }

    public static async Task StopOfficialBotAsync()
    {
        if (OfficialBot is null) return;
        await OfficialBot.StopAsync();
        await OfficialBot.DisposeAsync();
        OfficialBot = null;
        OnLog("[官方Bot] 已断开");
    }

    /// <summary>UI 日志流（所有页面可订阅）。</summary>
    public static event Action<string>? Logged;

    /// <summary>日志环形缓冲（最近 1000 条，日志页随时打开可看历史）。</summary>
    private static readonly ConcurrentQueue<string> LogBuffer = new();

    /// <summary>历史日志快照（旧→新，直接展示）。</summary>
    public static IReadOnlyList<string> GetRecentLogs() => LogBuffer.ToArray();

    /// <summary>启动后自动连接（按各自 Enabled 开关）：OneBot 服务 + 官方 Bot + NapCat 协议端。错误不抛出。</summary>
    public static async Task AutoConnectAsync()
    {
        var config = AppConfigStore.Load();
        if (!config.AutoStartBots) return;
        OnLog("[自动连接] 开始…");

        if (config.OneBot11.Enabled && !OneBotServerStarted)
        {
            try
            {
                await StartOneBot11Async();
            }
            catch (Exception ex)
            {
                OnLog($"[自动连接] OneBot 服务启动失败：{ex.Message}");
            }
        }

        if (config.OfficialBot.Enabled && !OfficialBotRunning)
        {
            try
            {
                await StartOfficialBotAsync();
            }
            catch (Exception ex)
            {
                OnLog($"[自动连接] 官方 Bot 连接失败：{ex.Message}");
            }
        }

        // NapCat 协议端（已配置启动脚本时）：补配置 + 拉起，OneBot 服务监听后它会自动连入
        if (config.OneBot11.Enabled && !string.IsNullOrWhiteSpace(config.NapCat.ScriptPath))
        {
            try
            {
                await StartNapCatAsync();
            }
            catch (Exception ex)
            {
                OnLog($"[自动连接] NapCat 启动失败：{ex.Message}");
            }
        }
        OnLog("[自动连接] 完成");
    }

    /// <summary>补入反向 WS 配置并拉起 NapCat（已运行则复用）。</summary>
    public static async Task StartNapCatAsync()
    {
        var config = AppConfigStore.Load();
        var manager = new NapCat.NapCatManager(config.NapCat);
        manager.ManagerLog += (_, line) => OnLog(line.StartsWith("[NapCat]") ? line : $"[NapCat] {line}");
        if (manager.IsRunning)
        {
            OnLog("[NapCat] 检测到已在运行，复用现有实例");
            return;
        }
        manager.InjectReverseWsConfig(config.OneBot11.Port, config.OneBot11.Token);
        await manager.StartAsync();
    }

    public static async Task StartOneBot11Async()
    {
        if (OneBotServerStarted) return;
        Config = AppConfigStore.Load();
        Host.Ensure();

        _oneBot = new OneBot11Connection(Config.OneBot11);
        _oneBot.ConnectionLog += (_, line) => OnLog(line);
        _oneBot.MessageReceived += async (_, e) =>
        {
            try
            {
                if (Runtime is not null && Config.OneBot11.Enabled)
                    await Runtime.OnMessageAsync(Bridge, e.Message);
            }
            catch (Exception ex)
            {
                OnLog($"[Runtime] 处理消息失败：{ex.Message}");
            }
        };

        await _oneBot.StartAsync();
        OneBotServerStarted = true;
        OnLog($"Bot 服务已启动：{_oneBot.Name}（等待 NapCat 连入）");
    }

    /// <summary>按最新配置同步 MCP 服务器连接（启动新增 / 重启变更 / 停止移除）。</summary>
    public static Task SyncMcpAsync(CancellationToken ct = default) => Host.SyncMcpAsync(ct);

    // ---------- 本机对话（对话页）：与 QQ 消息共用同一条 AgentRuntime 管线 ----------

    /// <summary>本机对话的固定会话 ID（会话库中以此区分，训练导出时也可识别）。</summary>
    public const string LocalChatId = AgentHost.LocalChatId;

    /// <summary>本机对话回复流（对话页订阅；Agent 回复经此而非 QQ 通道发出）。</summary>
    public static event Action<string>? LocalChatReply;

    /// <summary>确保本机对话管线可用（仅装配本地组件，不启动任何网络服务）。</summary>
    public static void EnsureLocalChat() => Host.Ensure();

    /// <summary>发送一条本机对话消息；回复分段经 <see cref="LocalChatReply"/> 事件返回。</summary>
    public static Task SendLocalChatAsync(string text, CancellationToken ct = default)
        => Host.SendLocalChatAsync(text, ct);

    /// <summary>应用退出：优雅停止所有连接与进程（防止 NapCat 等残留）。</summary>
    public static async Task ShutdownAsync()
    {
        OnLog("[退出] 正在停止所有连接…");
        try
        {
            var napCat = new NapCat.NapCatManager(AppConfigStore.Load().NapCat);
            await napCat.StopAsync();
        }
        catch (Exception ex)
        {
            OnLog($"[退出] NapCat 停止异常：{ex.Message}");
        }
        try
        {
            if (OneBotServerStarted) await StopOneBot11Async();
        }
        catch (Exception ex)
        {
            OnLog($"[退出] OneBot 服务停止异常：{ex.Message}");
        }
        try
        {
            if (OfficialBot is not null) await StopOfficialBotAsync();
        }
        catch (Exception ex)
        {
            OnLog($"[退出] 官方 Bot 断开异常：{ex.Message}");
        }
        try
        {
            if (_host is not null) await _host.DisposeAsync();
        }
        catch (Exception ex)
        {
            OnLog($"[退出] 运行时释放异常：{ex.Message}");
        }
        OnLog("[退出] 完成");
    }

    public static async Task StopOneBot11Async()
    {
        // 停服务前把活跃会话都切段（保住 L1 摘要）
        if (_host is not null) await _host.CutAllSessionsAsync("shutdown");

        if (_oneBot is not null)
        {
            await _oneBot.StopAsync();
            await _oneBot.DisposeAsync();
            _oneBot = null;
        }
        OneBotServerStarted = false;
        // 官方 Bot 还在运行时保留运行时（两者共用）；否则释放，下次启动按最新配置重新装配
        if (OfficialBot is null && _host is not null)
            await _host.DisposeAsync();
        OnLog("Bot 服务已停止");
    }

    private static void OnLog(string line)
    {
        var entry = $"{DateTime.Now:HH:mm:ss} {line}";
        LogBuffer.Enqueue(entry);
        while (LogBuffer.Count > 1000) LogBuffer.TryDequeue(out _);
        Logged?.Invoke(entry);
    }

    /// <summary>外部组件写入全局日志流（如 NapCat 进程输出、页面级日志）。</summary>
    public static void Log(string line) => OnLog(line);

    /// <summary>桥接：把 AgentRuntime 的回复通过 OneBot11 连接发回去。</summary>
    private sealed class CoreBridge : IBotConnectionFactory
    {
        public Task SendTextAsync(string chatId, ChatType chatType, string text, string? replyToMessageId, CancellationToken ct = default)
        {
            var conn = _oneBot;
            if (conn is null) throw new InvalidOperationException("OneBot11 连接未启动");
            return conn.SendTextAsync(chatId, chatType, text, replyToMessageId, ct);
        }

        public async Task<bool> TrySendForwardAsync(string chatId, ChatType chatType, IReadOnlyList<string> sections, CancellationToken ct = default)
        {
            var conn = _oneBot;
            if (conn is null) return false;
            return await conn.SendForwardAsync(chatId, chatType, sections, ct);
        }
    }
}
