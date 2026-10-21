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
/// 频道管理器 + AgentRuntime + 路由器的组合根。
/// 与 UI 无关的「运行时装配」已下沉到 <see cref="AgentHost"/>（Headless 复用同一份），这里只保留频道与日志桥接。
/// </summary>
public static class AppHost
{
    private static ChannelManager? _channels;
    private static string? _channelFingerprint;
    private static AgentHost? _host;

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

    /// <summary>官方 Bot（QQ 开放平台）连接实例；由频道管理器统一持有。</summary>
    public static OfficialBotConnection? OfficialBot { get; private set; }

    public static bool OfficialBotRunning => OfficialBot?.IsConnected ?? false;

    /// <summary>已发起连接但 WS 尚未 open（连接中状态）。</summary>
    public static bool OfficialBotPending => OfficialBot is not null && !OfficialBot.IsConnected;

    /// <summary>频道状态快照（仪表盘 / 设置页可直接展示）。</summary>
    public static IReadOnlyList<ChannelStatus> ChannelStatuses => _channels?.Snapshot() ?? [];

    /// <summary>启动官方 Bot 连接（凭证从最新配置读取，独立于 OneBot11 服务）。</summary>
    public static async Task<string> StartOfficialBotAsync()
    {
        if (OfficialBot is not null) return "官方 Bot 已在运行";

        var config = AppConfigStore.Load();
        Config = config;
        var channel = config.FindChannel(ChannelTransport.Official);
        if (channel is null) return "未配置官方 Bot 频道";
        var (appId, appSecret) = channel.ResolveOfficialCredentials();
        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(appSecret))
            return "请先填写并保存 AppID / AppSecret";

        Host.Ensure();
        var manager = await EnsureChannelsAsync(config);
        if (!await manager.StartAsync(channel.Id))
            return $"连接失败：{manager.Find(channel.Id)?.LastError ?? "未知错误"}";

        OfficialBot = manager.Find(channel.Id)?.Connection as OfficialBotConnection;
        OnLog($"[官方Bot] 已启动：{OfficialBot?.Name ?? channel.Name}");
        return "已连接";
    }

    public static async Task StopOfficialBotAsync()
    {
        var channel = AppConfigStore.Load().FindChannel(ChannelTransport.Official);
        if (_channels is not null && channel is not null)
            await _channels.StopAsync(channel.Id);
        OfficialBot = null;
        OnLog("[官方Bot] 已断开");
    }

    /// <summary>
    /// 从指定频道主动发送一条消息（不带 msg_id）。channelId 为空时选优先级最高且已启用的频道
    /// （官方 Bot 优先）。群聊主动消息需群主在 QQ 中开启「机器人主动在群聊内发言」。
    /// </summary>
    public static async Task<string> SendProactiveAsync(string chatId, ChatType chatType, string text,
        string? channelId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(chatId) || string.IsNullOrWhiteSpace(text))
            return "目标或内容为空";

        var config = AppConfigStore.Load();
        var channel = channelId is null
            ? config.ActiveChannels().FirstOrDefault()
            : config.Channels?.FirstOrDefault(c =>
                string.Equals(c.Id, channelId, StringComparison.OrdinalIgnoreCase));
        if (channel is null) return "未找到可用频道";

        var manager = await EnsureChannelsAsync(config);
        if (manager.Find(channel.Id) is not { Running: true } &&
            !await manager.StartAsync(channel.Id, ct))
            return $"频道启动失败：{manager.Find(channel.Id)?.LastError ?? "未知错误"}";
        SyncConnectionFields(manager);

        try
        {
            await manager.CreateSender(channel.Id).SendProactiveAsync(chatId, chatType, text, ct);
            return channel.IsOfficial && chatType == ChatType.Group
                ? "已提交发送（群聊主动消息需群主开启「机器人主动在群聊内发言」，否则平台会拒绝）"
                : "已提交发送（平台可能因用户主动消息开关或频控拒绝）";
        }
        catch (Exception ex)
        {
            return $"发送失败：{ex.Message}";
        }
    }

    /// <summary>UI 日志流（所有页面可订阅）。</summary>
    public static event Action<string>? Logged;

    /// <summary>日志环形缓冲（最近 1000 条，日志页随时打开可看历史）。</summary>
    private static readonly ConcurrentQueue<string> LogBuffer = new();

    /// <summary>历史日志快照（旧→新，直接展示）。</summary>
    public static IReadOnlyList<string> GetRecentLogs() => LogBuffer.ToArray();

    /// <summary>启动后按频道优先级自动连接（官方 Bot 优先，其次 OneBot / NapCat）。错误不抛出。</summary>
    public static async Task AutoConnectAsync()
    {
        var config = AppConfigStore.Load();
        if (!config.AutoStartBots) return;
        Config = config;
        OnLog("[自动连接] 开始…");

        var manager = await EnsureChannelsAsync(config);
        await manager.StartAllAsync();
        SyncConnectionFields(manager);

        // NapCat 协议端：只在 OneBot 频道启用且配置了启动脚本时拉起
        var oneBotChannel = config.FindChannel(ChannelTransport.OneBot11);
        if (oneBotChannel is { Enabled: true } && !string.IsNullOrWhiteSpace(config.NapCat.ScriptPath))
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
        var channel = config.FindChannel(ChannelTransport.OneBot11);
        var oneBot = channel?.OneBot11 ?? new OneBot11Config();
        var manager = new NapCat.NapCatManager(config.NapCat);
        manager.ManagerLog += (_, line) => OnLog(line.StartsWith("[NapCat]") ? line : $"[NapCat] {line}");
        if (manager.IsRunning)
        {
            OnLog("[NapCat] 检测到已在运行，复用现有实例");
            return;
        }
        manager.InjectReverseWsConfig(oneBot.Port, oneBot.Token);
        await manager.StartAsync();
    }

    public static async Task StartOneBot11Async()
    {
        if (OneBotServerStarted) return;
        var config = AppConfigStore.Load();
        Config = config;
        Host.Ensure();

        var channel = config.FindChannel(ChannelTransport.OneBot11);
        if (channel is null) throw new InvalidOperationException("未配置 OneBot11 频道");

        var manager = await EnsureChannelsAsync(config);
        if (!await manager.StartAsync(channel.Id))
            throw new InvalidOperationException(manager.Find(channel.Id)?.LastError ?? "OneBot11 启动失败");

        SyncConnectionFields(manager);
        OnLog($"Bot 服务已启动：{manager.Find(channel.Id)?.Connection?.Name ?? channel.Name}（等待 NapCat 连入）");
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

    /// <summary>应用退出：优雅停止所有频道与进程（防止 NapCat 等残留）。</summary>
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
            // 先停频道（把合并窗口里的消息放出去），再切段会话，避免退出时丢最后几条消息
            if (_channels is not null) await _channels.DisposeAsync();
            if (_host is not null) await _host.CutAllSessionsAsync("shutdown");
        }
        catch (Exception ex)
        {
            OnLog($"[退出] 频道停止异常：{ex.Message}");
        }
        _channels = null;
        _channelFingerprint = null;
        OneBotServerStarted = false;
        OfficialBot = null;
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

        var channel = AppConfigStore.Load().FindChannel(ChannelTransport.OneBot11);
        if (_channels is not null && channel is not null)
            await _channels.StopAsync(channel.Id);

        OneBotServerStarted = false;
        // 还有别的频道在跑就保留运行时；否则释放，下次启动按最新配置重新装配
        if (_channels is null || _channels.Channels.All(c => !c.Running))
        {
            if (_host is not null) await _host.DisposeAsync();
        }
        OnLog("Bot 服务已停止");
    }

    // ---------- 频道装配 ----------

    /// <summary>按最新配置装配频道管理器；配置发生实质变化时重建（避免改完凭证仍拿旧连接）。</summary>
    private static async Task<ChannelManager> EnsureChannelsAsync(AppConfig config)
    {
        var channels = config.Channels ?? [];
        var fingerprint = channels.ChannelFingerprint();
        if (_channels is not null && _channelFingerprint == fingerprint)
            return _channels;

        if (_channels is not null)
        {
            await _channels.DisposeAsync();
            _channels = null;
            OneBotServerStarted = false;
            OfficialBot = null;
        }

        var manager = new ChannelManager(channels);
        manager.Log += (_, line) => OnLog(line);
        manager.MessageReceived += OnChannelMessage;
        _channels = manager;
        _channelFingerprint = fingerprint;
        return manager;
    }

    private static void SyncConnectionFields(ChannelManager manager)
    {
        // 「已启动」看运行时，而不是 NapCat 是否已连入（服务在监听也算启动成功）。
        OneBotServerStarted = manager.Channels.Any(c => c.Config.IsOneBot11 && c.Running);
        OfficialBot = manager.Channels
            .Where(c => c.Config.IsOfficial && c.Running)
            .Select(c => c.Connection)
            .OfType<OfficialBotConnection>()
            .FirstOrDefault();
    }

    private static async void OnChannelMessage(object? sender, ChannelMessageEventArgs e)
    {
        try
        {
            Host.Ensure();
            if (Runtime is not null && _channels is not null)
                await Runtime.OnMessageAsync(_channels.CreateSender(e.ChannelId), e.Message);
        }
        catch (Exception ex)
        {
            OnLog($"[Runtime] 处理消息失败：{ex.Message}");
        }
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
}
