using System.Collections.Concurrent;
using System.IO;
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
/// </summary>
public static class AppHost
{
    private static OneBot11Connection? _oneBot;
    private static AgentRuntime? _runtime;
    private static SessionManager? _sessions;
    private static readonly CoreBridge Bridge = new();

    public static AppConfig Config { get; private set; } = AppConfigStore.Load();

    public static ModelRouter Router { get; } = new(Config);

    /// <summary>模型用量持久化（按日聚合，跨重启累计；仪表盘「模型调用」与今日累计数据源）。</summary>
    public static LogAgent.Memory.ModelUsageStore Usage { get; } =
        new(Path.Combine(AppConfigStore.GetConfigDirectory(), "data", "logagent.db"));

    static AppHost()
    {
        // 路由计量接线：每次模型调用 → 内存看板（本次启动）+ 数据库日聚合（跨重启）
        Router.Metered += (_, e) =>
        {
            Stats.Record(e);
            try { Usage.Record(e); }
            catch { /* 计量旁路，绝不影响聊天 */ }
        };
    }

    /// <summary>路由计量统计（仪表盘看板）。</summary>
    public static RouterStats Stats { get; } = new();

    public static SessionManager? Sessions => _sessions;

    public static Sandbox.SandboxService? Sandbox => _sandbox;

    public static Knowledge.KnowledgeStore? Knowledge => _knowledge;

    public static Skills.SkillRegistry? Skills => _skills;

    /// <summary>MCP 服务器管理器（标准协议客户端）。</summary>
    public static LogAgent.Mcp.McpManager? Mcp => _mcp;

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
                EnsureRuntime();
                if (_runtime is not null)
                    await _runtime.OnMessageAsync(Bridge, e.Message);
            }
            catch (Exception ex)
            {
                OnLog($"[官方Bot] 处理消息失败：{ex.Message}");
            }
        };
        EnsureRuntime();
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
    private static readonly System.Collections.Concurrent.ConcurrentQueue<string> LogBuffer = new();

    /// <summary>历史日志快照（新→旧顺序为旧→新，直接展示）。</summary>
    public static IReadOnlyList<string> GetRecentLogs() => LogBuffer.ToArray();

    private static Sandbox.SandboxService? _sandbox;
    private static Knowledge.KnowledgeStore? _knowledge;
    private static Skills.SkillRegistry? _skills;
    private static LogAgent.Mcp.McpManager? _mcp;

    private static LearningAgent? _learning;

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
        EnsureRuntime();

        _oneBot = new OneBot11Connection(Config.OneBot11);
        _oneBot.ConnectionLog += (_, line) => OnLog(line);
        _oneBot.MessageReceived += async (_, e) =>
        {
            try
            {
                if (_runtime is not null && Config.OneBot11.Enabled)
                    await _runtime.OnMessageAsync(Bridge, e.Message);
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

    private static bool _runtimeInitializing;

    /// <summary>
    /// 装配 Agent 运行时（会话/沙箱/知识库/学习/路由），OneBot 与官方 Bot 共用。
    /// 防重入：装配期间（_runtime 尚未赋值）的任何调用直接返回——历史事故：SyncMcpAsync
    /// 在装配中途被同步调用，其内部回调 EnsureRuntime 时 _runtime 仍为 null → 无限递归 → 栈溢出崩溃。
    /// </summary>
    private static void EnsureRuntime()
    {
        if (_runtime is not null || _runtimeInitializing) return;
        _runtimeInitializing = true;
        try
        {
            BuildRuntime();
        }
        finally
        {
            _runtimeInitializing = false;
        }
    }

    private static void BuildRuntime()
    {
        Config = AppConfigStore.Load();

        var dbPath = Path.Combine(AppConfigStore.GetConfigDirectory(), "data", "logagent.db");
        var db = new Database(dbPath);
        db.EnsureCreated();
        _sessions = new SessionManager(new SessionStore(db), Router, Config.Agent);
        _sessions.SessionLog += (_, line) => OnLog($"[会话] {line}");

        _sandbox = new Sandbox.SandboxService(
            Config.Sandbox,
            Path.Combine(AppConfigStore.GetConfigDirectory(), "sandbox"));
        _sandbox.Audited += (_, e) => OnLog(
            $"[沙箱] {(e.Allowed ? $"{e.Command}" : $"拒绝 {e.Command}：{e.RejectReason}")}");

        _knowledge = new Knowledge.KnowledgeStore(
            Path.Combine(AppConfigStore.GetConfigDirectory(), "data", "knowledge.db"),
            Config.Knowledge,
            Router);
        _knowledge.EnsureCreated();
        _knowledge.KnowledgeLog += (_, line) => OnLog($"[知识库] {line}");

        _learning = new LearningAgent(Router, _knowledge, new Knowledge.WebFetcher(), Config.Knowledge);
        _learning.LearningLog += (_, line) => OnLog($"[学习] {line}");

        // 技能：内置目录（随安装包）+ 用户目录（%AppData%，商店安装）
        _skills = new Skills.SkillRegistry(
            Path.Combine(AppContext.BaseDirectory, "skills"),
            Path.Combine(AppConfigStore.GetConfigDirectory(), "skills"));
        _skills.Reload();
        _skills.SkillLog += (_, line) => OnLog($"[技能] {line}");

        // MCP：标准协议客户端（stdio / HTTP 双传输，服务器工具自动暴露）
        _mcp = new LogAgent.Mcp.McpManager();
        _mcp.Log += (_, line) => OnLog($"[MCP] {line}");

        // 文件暂存 + 内置文件工具：QQ 收到的大文件/抓取日志完整落盘，
        // 模型用 read_file / search_file / run_shell 自己查看任意部分（中间不省略）
        var filesDir = Path.Combine(AppConfigStore.GetConfigDirectory(), "files");
        Directory.CreateDirectory(filesDir);
        var cleanedCount = LogAgent.Skills.FileTools.CleanupOldFiles(filesDir, TimeSpan.FromDays(7));
        if (cleanedCount > 0) OnLog($"[文件] 已清理 {cleanedCount} 个过期暂存文件");
        var fileTools = new LogAgent.Skills.FileTools(filesDir, Config.Sandbox.Enabled ? _sandbox.Runner : null);
        fileTools.ToolLog += (_, line) => OnLog($"[工具] {line}");

        var hub = new Skills.ToolHostHub(_skills, _mcp, Config, fileTools, new Skills.WebTools());

        var groupAgent = new GroupContextAgent(Router, _knowledge, Config.Knowledge, hub, Config.Skills);
        groupAgent.AgentLog += (_, line) => OnLog($"[技能] {line}");

        // 消息富化（logshare 链接 / QQ 文件附件 → 抓取正文并入消息）
        var enricher = new MessageEnricher(filesDir: filesDir);
        enricher.EnrichLog += (_, line) => OnLog($"[富化] {line}");

        var logAgent = new LogAnalysisAgent(Router, _knowledge, Config.Knowledge, hub, Config.Skills, filesDir);
        logAgent.AnalysisLog += (_, line) => OnLog($"[分析] {line}");

        _runtime = new AgentRuntime(
            Router,
            groupAgent,
            logAgent,
            _learning,
            _sessions,
            Config.Agent,
            enricher);
        _runtime.RuntimeLog += (_, line) => OnLog($"[Runtime] {line}");

        // 运行时就绪后再异步建立 MCP 连接（此刻 _runtime 已赋值，SyncMcpAsync 内的 EnsureRuntime 直接返回，不会重入）
        _ = SyncMcpAsync();
    }

    /// <summary>按最新配置同步 MCP 服务器连接（启动新增 / 重启变更 / 停止移除）。</summary>
    public static async Task SyncMcpAsync(CancellationToken ct = default)
    {
        EnsureRuntime();
        if (_mcp is null) return;
        try
        {
            await _mcp.SyncAsync(Config.Mcp.Servers, ct);
        }
        catch (Exception ex)
        {
            OnLog($"[MCP] 同步失败：{ex.Message}");
        }
    }

    // ---------- 本机对话（对话页）：与 QQ 消息共用同一条 AgentRuntime 管线 ----------

    /// <summary>本机对话的固定会话 ID（会话库中以此区分，训练导出时也可识别）。</summary>
    public const string LocalChatId = "local-console";

    /// <summary>本机对话回复流（对话页订阅；Agent 回复经此而非 QQ 通道发出）。</summary>
    public static event Action<string>? LocalChatReply;

    private static readonly LocalChatBridge LocalBridge = new();

    /// <summary>确保本机对话管线可用（仅装配本地组件，不启动任何网络服务）。</summary>
    public static void EnsureLocalChat() => EnsureRuntime();

    /// <summary>发送一条本机对话消息；回复分段经 <see cref="LocalChatReply"/> 事件返回。</summary>
    public static async Task SendLocalChatAsync(string text, CancellationToken ct = default)
    {
        EnsureRuntime();
        var message = new BotMessage
        {
            MessageId = $"local-{Guid.NewGuid():N}",
            ChatId = LocalChatId,
            ChatType = ChatType.Private,
            SenderId = "console",
            SenderName = "本机",
            Text = text,
        };
        await _runtime!.OnMessageAsync(LocalBridge, message, ct);
    }

    /// <summary>本地对话桥：回复不经网络，直接投递给对话页订阅者。合并转发 = 整段一次投递（页面可滚动）。</summary>
    private sealed class LocalChatBridge : IBotConnectionFactory
    {
        public Task SendTextAsync(string chatId, ChatType chatType, string text, string? replyToMessageId, CancellationToken ct = default)
        {
            if (chatId == LocalChatId) LocalChatReply?.Invoke(text);
            return Task.CompletedTask;
        }

        public Task<bool> TrySendForwardAsync(string chatId, ChatType chatType, IReadOnlyList<string> sections, CancellationToken ct = default)
        {
            if (chatId != LocalChatId) return Task.FromResult(false);
            LocalChatReply?.Invoke(string.Join("\n\n", sections));
            return Task.FromResult(true);
        }
    }

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
        OnLog("[退出] 完成");
    }

    public static async Task StopOneBot11Async()
    {
        // 停服务前把活跃会话都切段（保住 L1 摘要）
        if (_sessions is not null)
        {
            foreach (var chat in _sessions.GetActiveChats())
                await _sessions.CutAsync(chat, "shutdown");
        }
        if (_oneBot is not null)
        {
            await _oneBot.StopAsync();
            await _oneBot.DisposeAsync();
            _oneBot = null;
        }
        OneBotServerStarted = false;
        // 官方 Bot 还在运行时保留 Agent 运行时（两者共用）
        if (OfficialBot is null)
        {
            _runtime = null;
            _learning = null;
            _knowledge = null;
            _sandbox = null;
            _sessions = null;
        }
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

/// <summary>Provider 级计量累计：调用数/成功/失败/token/平均延迟。</summary>
public sealed class RouterStats
{
    public sealed record ProviderStat(string Provider, string Model, long Ok, long Fail,
        long PromptTokens, long CompletionTokens, double AvgLatencyMs);

    private readonly ConcurrentDictionary<string, (long Ok, long Fail, long P, long C, double LatencySum, string Model)> _stats = new();

    public void Record(RouterMeterEvent e)
    {
        _stats.AddOrUpdate(e.ProviderId,
            (e.Success ? 1 : 0, e.Success ? 0 : 1, e.PromptTokens, e.CompletionTokens,
                e.Latency.TotalMilliseconds, e.Model),
            (_, v) => (v.Ok + (e.Success ? 1 : 0), v.Fail + (e.Success ? 0 : 1),
                v.P + e.PromptTokens, v.C + e.CompletionTokens,
                v.LatencySum + e.Latency.TotalMilliseconds, e.Model));
    }

    public List<ProviderStat> Snapshot() =>
        _stats.Select(kv => new ProviderStat(
            kv.Key, kv.Value.Model, kv.Value.Ok, kv.Value.Fail,
            kv.Value.P, kv.Value.C,
            kv.Value.Ok > 0 ? kv.Value.LatencySum / kv.Value.Ok : 0)).ToList();
}
