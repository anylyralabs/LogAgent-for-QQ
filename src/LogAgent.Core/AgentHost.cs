using LogAgent.Knowledge;
using LogAgent.Mcp;
using LogAgent.Memory;
using LogAgent.Routing;
using LogAgent.Sandbox;
using LogAgent.Shared.Configuration;
using LogAgent.Shared.Messaging;
using LogAgent.Skills;

namespace LogAgent.Core;

/// <summary>
/// Agent 运行时组合根（与 UI 无关）：装配会话 / 沙箱 / 知识库 / 学习 / 技能 / MCP / 文件工具 / Agent，
/// 并把路由计量、日志、MCP 同步接好。桌面壳（WPF / Avalonia）与无头宿主（Headless serve）共用这一份，
/// 保证「同一条处理管线」。
/// </summary>
public sealed class AgentHost : IAsyncDisposable
{
    private AgentRuntime? _runtime;
    private SessionManager? _sessions;
    private LearningAgent? _learning;
    private SandboxService? _sandbox;
    private KnowledgeStore? _knowledge;
    private SkillRegistry? _skills;
    private McpManager? _mcp;
    private MessageEnricher? _enricher;
    private bool _initializing;
    private readonly LocalChatBridge _localBridge = new();

    /// <summary>当前配置（装配时重新读取，保证与磁盘一致）。</summary>
    public AppConfig Config { get; private set; }

    /// <summary>模型路由（进程内单例，计量事件在构造时接好）。</summary>
    public ModelRouter Router { get; }

    /// <summary>模型用量持久化（按日聚合，跨重启累计）。</summary>
    public ModelUsageStore Usage { get; }

    /// <summary>路由计量统计（本次启动内存看板）。</summary>
    public RouterStats Stats { get; } = new();

    public AgentHost(AppConfig? config = null, ModelRouter? router = null)
    {
        Config = config ?? AppConfigStore.Load();
        Router = router ?? new ModelRouter(Config);
        Usage = new ModelUsageStore(Path.Combine(AppConfigStore.GetDataDirectory(), "logagent.db"));
        _localBridge.Reply += text => LocalChatReply?.Invoke(text);

        Router.Metered += (_, e) =>
        {
            Stats.Record(e);
            try { Usage.Record(e); }
            catch { /* 计量旁路，绝不影响聊天 */ }
        };
    }

    public AgentRuntime? Runtime => _runtime;
    public SessionManager? Sessions => _sessions;
    public SandboxService? Sandbox => _sandbox;
    public KnowledgeStore? Knowledge => _knowledge;
    public SkillRegistry? Skills => _skills;
    public McpManager? Mcp => _mcp;

    /// <summary>运行时就绪（装配成功）。</summary>
    public bool IsReady => _runtime is not null;

    /// <summary>装配期间（_runtime 尚未赋值）的任何调用直接返回。</summary>
    public void Ensure()
    {
        if (_runtime is not null || _initializing) return;
        _initializing = true;
        try
        {
            Build();
        }
        finally
        {
            _initializing = false;
        }
    }

    /// <summary>按最新配置同步 MCP 服务器连接（启动新增 / 重启变更 / 停止移除）。</summary>
    public async Task SyncMcpAsync(CancellationToken ct = default)
    {
        Ensure();
        if (_mcp is null) return;
        try
        {
            await _mcp.SyncAsync(Config.Mcp.Servers, ct);
        }
        catch (Exception ex)
        {
            Log($"[MCP] 同步失败：{ex.Message}");
        }
    }

    /// <summary>全量日志流（宿主订阅后写 UI / stdout / 文件）。</summary>
    public event Action<string>? Logged;

    /// <summary>本机对话回复流（桌面「对话页」订阅；QQ 通道不走这里）。</summary>
    public event Action<string>? LocalChatReply;

    /// <summary>本机对话的固定会话 ID（会话库中以此区分，训练导出时也可识别）。</summary>
    public const string LocalChatId = "local-console";

    /// <summary>发送一条本机对话消息；回复经 <see cref="LocalChatReply"/> 返回。</summary>
    public async Task SendLocalChatAsync(string text, CancellationToken ct = default)
    {
        Ensure();
        var message = new BotMessage
        {
            MessageId = $"local-{Guid.NewGuid():N}",
            ChatId = LocalChatId,
            ChatType = ChatType.Private,
            SenderId = "console",
            SenderName = "本机",
            Text = text,
            ChannelName = "本机对话",
        };
        await _runtime!.OnMessageAsync(_localBridge, message, ct);
    }

    /// <summary>切段所有活跃会话（停机 / 重启前调用，保证 L1 摘要落库）。</summary>
    public async Task CutAllSessionsAsync(string reason = "shutdown", CancellationToken ct = default)
    {
        if (_sessions is null) return;
        foreach (var chat in _sessions.GetActiveChats())
            await _sessions.CutAsync(chat, reason, ct);
    }

    /// <summary>释放运行时持有的连接（MCP 子进程等）。</summary>
    public async ValueTask DisposeAsync()
    {
        if (_mcp is not null)
        {
            try { await _mcp.DisposeAsync(); }
            catch (Exception) { /* 退出路径尽力而为 */ }
        }
        _mcp = null;
        _runtime = null;
        _sessions = null;
        _learning = null;
        _knowledge = null;
        _sandbox = null;
        _skills = null;
        _enricher = null;
    }

    private void Build()
    {
        Config = AppConfigStore.Load();

        var db = new Database(Path.Combine(AppConfigStore.GetDataDirectory(), "logagent.db"));
        db.EnsureCreated();
        _sessions = new SessionManager(new SessionStore(db), Router, Config.Agent);
        _sessions.SessionLog += (_, line) => Log($"[会话] {line}");

        _sandbox = new SandboxService(Config.Sandbox, AppConfigStore.GetSandboxDirectory());
        _sandbox.Audited += (_, e) => Log(
            $"[沙箱] {(e.Allowed ? $"{e.Command}" : $"拒绝 {e.Command}：{e.RejectReason}")}");

        _knowledge = new KnowledgeStore(
            Path.Combine(AppConfigStore.GetDataDirectory(), "knowledge.db"),
            Config.Knowledge,
            Router);
        _knowledge.EnsureCreated();
        _knowledge.KnowledgeLog += (_, line) => Log($"[知识库] {line}");

        _learning = new LearningAgent(Router, _knowledge, new WebFetcher(), Config.Knowledge);
        _learning.LearningLog += (_, line) => Log($"[学习] {line}");

        // 技能：内置目录（随程序）+ 用户目录（数据目录，商店安装）
        _skills = new SkillRegistry(
            Path.Combine(AppContext.BaseDirectory, "skills"),
            AppConfigStore.GetSkillsDirectory());
        _skills.Reload();
        _skills.SkillLog += (_, line) => Log($"[技能] {line}");

        // MCP：标准协议客户端（stdio / HTTP 双传输，服务器工具自动暴露）
        _mcp = new McpManager();
        _mcp.Log += (_, line) => Log($"[MCP] {line}");

        // 文件暂存 + 内置文件工具：QQ 收到的大文件/抓取日志完整落盘，
        // 模型用 read_file / search_file / run_shell 自己查看任意部分（中间不省略）
        var filesDir = AppConfigStore.GetFilesDirectory();
        Directory.CreateDirectory(filesDir);
        var cleanedCount = FileTools.CleanupOldFiles(filesDir, TimeSpan.FromDays(7));
        if (cleanedCount > 0) Log($"[文件] 已清理 {cleanedCount} 个过期暂存文件");
        var fileTools = new FileTools(filesDir, Config.Sandbox.Enabled ? _sandbox.Runner : null);
        fileTools.ToolLog += (_, line) => Log($"[工具] {line}");

        var hub = new ToolHostHub(_skills, _mcp, Config, fileTools, new WebTools());

        var groupAgent = new GroupContextAgent(Router, _knowledge, Config.Knowledge, hub, Config.Skills);
        groupAgent.AgentLog += (_, line) => Log($"[技能] {line}");

        // 消息富化（logshare 链接 / QQ 文件附件 → 抓取正文并入消息）
        _enricher = new MessageEnricher(filesDir: filesDir);
        _enricher.EnrichLog += (_, line) => Log($"[富化] {line}");

        var logAgent = new LogAnalysisAgent(Router, _knowledge, Config.Knowledge, hub, Config.Skills, filesDir);
        logAgent.AnalysisLog += (_, line) => Log($"[分析] {line}");

        _runtime = new AgentRuntime(
            Router,
            groupAgent,
            logAgent,
            _learning,
            _sessions,
            Config.Agent,
            _enricher);
        _runtime.RuntimeLog += (_, line) => Log($"[Runtime] {line}");

        // 运行时就绪后再异步建立 MCP 连接（此刻 _runtime 已赋值，同步回调里的 Ensure 直接返回，不会重入）
        _ = SyncMcpAsync();
    }

    private void Log(string line) => Logged?.Invoke(line);

    /// <summary>本地对话桥：回复不经网络，直接投递给订阅者。合并转发 = 整段一次投递（页面可滚动）。</summary>
    private sealed class LocalChatBridge : IBotConnectionFactory
    {
        public event Action<string>? Reply;

        public Task SendTextAsync(string chatId, ChatType chatType, string text,
            string? replyToMessageId, CancellationToken ct = default)
        {
            if (chatId == LocalChatId) Reply?.Invoke(text);
            return Task.CompletedTask;
        }

        public Task<bool> TrySendForwardAsync(string chatId, ChatType chatType,
            IReadOnlyList<string> sections, string? replyToMessageId = null, CancellationToken ct = default)
        {
            if (chatId != LocalChatId) return Task.FromResult(false);
            Reply?.Invoke(string.Join("\n\n", sections));
            return Task.FromResult(true);
        }
    }
}
