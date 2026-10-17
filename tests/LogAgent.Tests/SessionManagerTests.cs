using LogAgent.Memory;
using LogAgent.Routing;
using LogAgent.Shared.Chat;
using LogAgent.Shared.Configuration;
using LogAgent.Shared.Messaging;
using Xunit;

namespace LogAgent.Tests;

public class SessionManagerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Database _db;

    public SessionManagerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"la-sess-{Guid.NewGuid():N}.db");
        _db = new Database(_dbPath);
        _db.EnsureCreated();
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch (IOException) { }
    }

    private static BotMessage Msg(string chatId, string text, ChatType type = ChatType.Group, string sender = "柠枺") =>
        new()
        {
            MessageId = Guid.NewGuid().ToString("N"),
            ChatId = chatId,
            ChatType = type,
            SenderId = "1",
            SenderName = sender,
            Text = text,
            MentionedMe = true,
        };

    private (SessionManager Manager, List<RouterMeterEvent> Events) Make(
        AgentConfig? config = null, bool summaryOk = true)
    {
        config ??= new AgentConfig();
        var appConfig = new AppConfig
        {
            Providers = [new ProviderConfig { Id = "s1", Name = "S1", ChatModel = "cheap", Enabled = true }],
            Rules = [new RouterRule { TaskType = TaskType.Summary, ProviderChain = ["s1"] }],
        };
        var router = new ModelRouter(appConfig, _ => new FakeClient("s1", summaryOk, reply: "这是摘要"));
        var manager = new SessionManager(new SessionStore(new Database(_dbPath)), router, config);
        return (manager, []);
    }

    [Fact]
    public async Task IncomingMessage_CreatesSession_AndStoresRaw()
    {
        var (manager, _) = Make();
        var ctx = await manager.OnIncomingAsync(Msg("777", "大家好"));
        Assert.NotEmpty(ctx.SessionId);
        Assert.Single(ctx.WindowTurns);
        var store = new SessionStore(new Database(_dbPath));
        Assert.Equal(1, store.ListSessions().Single(s => s.ChatId == "777").MessageCount);
    }

    [Fact]
    public async Task IdleTimeout_CutsSession_AndStartsNew()
    {
        var config = new AgentConfig { SessionIdleTimeoutMinutes = 1, SummaryOnCut = false };
        var (manager, _) = Make(config);
        await manager.OnIncomingAsync(Msg("777", "第一条"));

        // 模拟静默超时：通过第二次进入时 LastActiveAt 已过期的手段——直接调用 CutAsync 验证行为
        var cutId = await manager.CutAsync("777", "idle");
        Assert.NotNull(cutId);

        var store = new SessionStore(new Database(_dbPath));
        var closed = store.ListSessions().Single(s => s.Id == cutId);
        Assert.Equal("idle", closed.CutReason);
        Assert.NotNull(closed.EndTime);

        // 新消息进入新会话
        var ctx = await manager.OnIncomingAsync(Msg("777", "新会话消息"));
        var sessions = store.ListSessions().Where(s => s.ChatId == "777").ToList();
        Assert.Equal(2, sessions.Count);
        Assert.NotEqual(cutId, ctx.SessionId);
    }

    [Fact]
    public async Task CapacityCut_ByMaxMessages()
    {
        var config = new AgentConfig { SessionMaxMessages = 3, SessionMaxTokens = 999999, SummaryOnCut = false };
        var (manager, _) = Make(config);
        // 前 3 条填满会话，第 4 条触发容量切段进入新会话
        for (var i = 1; i <= 4; i++)
            await manager.OnIncomingAsync(Msg("777", $"消息{i}"));

        var store = new SessionStore(new Database(_dbPath));
        // 第 4 条消息前触发容量切段 → 应有 2 个会话
        Assert.Equal(2, store.ListSessions().Count(s => s.ChatId == "777"));
        var reasons = store.ListSessions().Select(s => s.CutReason).ToList();
        Assert.Contains("capacity", reasons);
    }

    [Fact]
    public async Task CutWithSummary_GeneratesL1_AndLoadsIntoContext()
    {
        var (manager, _) = Make(new AgentConfig { SummaryOnCut = true });
        await manager.OnIncomingAsync(Msg("777", "讨论日志错误 ABC"));
        await manager.CutAsync("777", "manual");

        var store = new SessionStore(new Database(_dbPath));
        Assert.Equal("这是摘要", store.ListSessions().Single(s => s.ChatId == "777").Summary);

        // 新会话的上下文应包含该摘要
        var ctx = await manager.OnIncomingAsync(Msg("777", "继续聊"));
        Assert.Contains("这是摘要", ctx.RecentSummaries);
    }

    [Fact]
    public async Task AgentReply_RecordedAsTurn_AndWindowAlternates()
    {
        var (manager, _) = Make(new AgentConfig { SummaryOnCut = false });
        var msg = Msg("777", "在吗");
        await manager.OnIncomingAsync(msg);
        manager.OnAgentReply(msg, "在的", modelUsed: "cheap");

        var store = new SessionStore(new Database(_dbPath));
        var ctx = await manager.OnIncomingAsync(Msg("777", "好"));
        Assert.Equal(3, ctx.WindowTurns.Count); // user, assistant, user
        Assert.Equal("assistant", ctx.WindowTurns[1].Role);
    }
}
