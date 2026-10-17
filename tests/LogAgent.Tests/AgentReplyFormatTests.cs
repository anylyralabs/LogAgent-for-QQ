using LogAgent.Core;
using LogAgent.Memory;
using LogAgent.Routing;
using LogAgent.Shared.Configuration;
using LogAgent.Shared.Messaging;
using Xunit;

namespace LogAgent.Tests;

// ─────────────────────────── 回复发送：分节 → 合并转发 → 回退 ───────────────────────────

public class AgentReplyFormatTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"la-fmt-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_dbPath + suffix); } catch (IOException) { }
    }

    private (AgentRuntime Runtime, FormatTestBridge Bridge) Make(string reply)
    {
        var appConfig = new AppConfig
        {
            Providers = [new ProviderConfig { Id = "p1", Name = "P1", ChatModel = "m", Enabled = true }],
            Rules = [new RouterRule { TaskType = TaskType.Chat, ProviderChain = ["p1"] }],
        };
        var router = new ModelRouter(appConfig, _ => new FakeClient("p1", true, reply: reply));
        var db = new Database(_dbPath);
        db.EnsureCreated();
        var sessions = new SessionManager(new SessionStore(db), router, new AgentConfig());
        var runtime = new AgentRuntime(
            router,
            new GroupContextAgent(router),
            new LogAnalysisAgent(router),
            null,
            sessions,
            new AgentConfig());
        return (runtime, new FormatTestBridge());
    }

    private static BotMessage Msg(string text) => new()
    {
        MessageId = "m1", ChatId = "777", ChatType = ChatType.Group,
        SenderId = "1", SenderName = "柠枺", Text = text, MentionedMe = true,
    };

    // ---------- 分节解析 ----------

    [Fact]
    public void SplitSections_ParsesTaggedSections()
    {
        var text = "📋 报告\n· 概览行\n\n【结论】崩了\n【原因分析】1. x\n2. y\n【解决方法】1. 换渲染器";
        var sections = AgentRuntime.SplitSections(text);

        Assert.Equal(4, sections.Count);
        Assert.StartsWith("📋 报告", sections[0]);
        Assert.StartsWith("【结论】", sections[1]);
        Assert.StartsWith("【原因分析】", sections[2]);
        Assert.StartsWith("【解决方法】", sections[3]);
    }

    [Fact]
    public void SplitSections_FewerThanTwoTags_NoSplit()
    {
        Assert.Empty(AgentRuntime.SplitSections("普通聊天文本\n【单个】标签不算"));
        Assert.Empty(AgentRuntime.SplitSections("完全普通文本"));
    }

    [Fact]
    public void SplitSections_BareHeaders_AlsoSplitAndNormalized()
    {
        // 部分模型不守【】格式，用裸标题（如 turn #149 的真实回复）
        var text = "桑桑这份日志很明确，是光影打架。\n\n结论\n· 根因：Iris\n\n解决方法\n1. 换渲染器";
        var sections = AgentRuntime.SplitSections(text);

        Assert.Equal(3, sections.Count);
        Assert.StartsWith("桑桑", sections[0]);
        Assert.StartsWith("【结论】", sections[1]); // 裸标题被规范化
        Assert.StartsWith("【解决方法】", sections[2]);
    }

    // ---------- 发送路由 ----------

    [Fact]
    public async Task StructuredReply_GoesThroughForward()
    {
        var reply = "📋 报告\n\n【结论】A\n【原因分析】B\n【解决方法】C";
        var (runtime, bridge) = Make(reply);
        await runtime.OnMessageAsync(bridge, Msg("你好"));

        var fwd = Assert.Single(bridge.Forwards);
        Assert.Equal(4, fwd.Count); // 概览节 + 三个【标题】节
        Assert.StartsWith("📋 报告", fwd[0]);
        Assert.Empty(bridge.Sent); // 不再刷屏分段
    }

    [Fact]
    public async Task ForwardUnsupported_FallsBackToText()
    {
        var reply = "【结论】A\n【原因分析】B";
        var (runtime, bridge) = Make(reply);
        bridge.ForwardSupported = false;
        await runtime.OnMessageAsync(bridge, Msg("你好"));

        Assert.Empty(bridge.Forwards);
        var sent = Assert.Single(bridge.Sent);
        Assert.Contains("【结论】", sent);
    }

    [Fact]
    public async Task PlainShortReply_SentDirectly()
    {
        var (runtime, bridge) = Make("你好呀～");
        await runtime.OnMessageAsync(bridge, Msg("你好"));

        Assert.Empty(bridge.Forwards);
        Assert.Equal("你好呀～", Assert.Single(bridge.Sent));
    }

    [Fact]
    public async Task LongPlainText_UsesChunkedForward()
    {
        var line = new string('x', 300);
        var reply = string.Join("\n", Enumerable.Repeat(line, 8)); // ~2400 字，无【】分节
        var (runtime, bridge) = Make(reply);
        await runtime.OnMessageAsync(bridge, Msg("给我讲个长故事"));

        var fwd = Assert.Single(bridge.Forwards);
        Assert.True(fwd.Count >= 2); // 按行边界切块合并转发
        Assert.All(fwd, c => Assert.True(c.Length <= 1300, $"chunk too long: {c.Length}"));
        Assert.Empty(bridge.Sent);
    }
}

/// <summary>可编程的测试桥：记录普通发送与合并转发。</summary>
internal sealed class FormatTestBridge : IBotConnectionFactory
{
    public List<string> Sent { get; } = [];
    public List<IReadOnlyList<string>> Forwards { get; } = [];
    public bool ForwardSupported { get; set; } = true;

    public Task SendTextAsync(string chatId, ChatType chatType, string text, string? replyToMessageId, CancellationToken ct = default)
    {
        Sent.Add(text);
        return Task.CompletedTask;
    }

    public Task<bool> TrySendForwardAsync(string chatId, ChatType chatType, IReadOnlyList<string> sections, CancellationToken ct = default)
    {
        if (!ForwardSupported) return Task.FromResult(false);
        Forwards.Add(sections);
        return Task.FromResult(true);
    }
}
