using LogAgent.Core;
using LogAgent.Memory;
using LogAgent.Routing;
using LogAgent.Shared.Configuration;
using LogAgent.Shared.Messaging;
using Xunit;

namespace LogAgent.Tests;

internal sealed class RecordingBridge : IBotConnectionFactory
{
    public List<(string ChatId, string Text)> Sent { get; } = [];

    public Task SendTextAsync(string chatId, ChatType chatType, string text, string? replyToMessageId, CancellationToken ct = default)
    {
        Sent.Add((chatId, text));
        return Task.CompletedTask;
    }
}

public class AgentRuntimeTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"la-rt-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch (IOException) { }
    }

    private static BotMessage Msg(string text, ChatType type = ChatType.Group, bool mentioned = false) =>
        new()
        {
            MessageId = "m1",
            ChatId = "777",
            ChatType = type,
            SenderId = "1",
            SenderName = "柠枺",
            Text = text,
            MentionedMe = mentioned,
        };

    private (AgentRuntime Runtime, RecordingBridge Bridge) Make(AgentConfig? config = null, string logAnalysisReply = "测试回复")
    {
        config ??= new AgentConfig();
        var appConfig = new AppConfig
        {
            Providers = [new ProviderConfig { Id = "p1", Name = "P1", ChatModel = "m", Enabled = true }],
            Rules =
            [
                new RouterRule { TaskType = TaskType.Chat, ProviderChain = ["p1"] },
                new RouterRule { TaskType = TaskType.LogAnalysis, ProviderChain = ["p1"] },
                new RouterRule { TaskType = TaskType.DeepReasoning, ProviderChain = ["p1"] },
                new RouterRule { TaskType = TaskType.Summary, ProviderChain = ["p1"] },
            ],
        };
        var router = new ModelRouter(appConfig, _ => new FakeClient("p1", true)
        {
            ReplySelector = input => input.Contains("资深运维") ? logAnalysisReply : "测试回复",
        });
        var db = new Database(_dbPath);
        db.EnsureCreated();
        var sessions = new SessionManager(new SessionStore(db), router, config);
        var runtime = new AgentRuntime(
            router,
            new GroupContextAgent(router),
            new LogAnalysisAgent(router),
            learningAgent: null,
            sessions,
            config);
        var bridge = new RecordingBridge();
        return (runtime, bridge);
    }

    [Fact]
    public async Task GroupMessage_WithoutAtOrKeyword_IsIgnored()
    {
        var (runtime, bridge) = Make();
        await runtime.OnMessageAsync(bridge, Msg("普通闲聊"));
        Assert.Empty(bridge.Sent);
    }

    [Fact]
    public async Task GroupMessage_WithAt_Replies()
    {
        var (runtime, bridge) = Make();
        await runtime.OnMessageAsync(bridge, Msg("@机器人 你好", mentioned: true));
        Assert.Single(bridge.Sent);
        Assert.Equal("测试回复", bridge.Sent[0].Text);
    }

    [Fact]
    public async Task PrivateMessage_AlwaysReplies()
    {
        var (runtime, bridge) = Make();
        await runtime.OnMessageAsync(bridge, Msg("你好", ChatType.Private));
        Assert.Single(bridge.Sent);
    }

    [Fact]
    public async Task TriggerKeyword_EvenWithoutAt_Replies()
    {
        var (runtime, bridge) = Make(new AgentConfig
        {
            NeedAtInGroup = true,
            TriggerKeywords = ["日志", "报错"],
        });
        await runtime.OnMessageAsync(bridge, Msg("这个报错怎么处理"));
        Assert.Single(bridge.Sent);
    }

    [Fact]
    public async Task NewTopicCommand_CutsSession_AndAcknowledges()
    {
        var (runtime, bridge) = Make(new AgentConfig { SummaryOnCut = false });
        await runtime.OnMessageAsync(bridge, Msg("@机器人 先聊点啥", mentioned: true));
        await runtime.OnMessageAsync(bridge, Msg("新话题"));
        Assert.Equal(2, bridge.Sent.Count);
        Assert.Contains("新话题", bridge.Sent[1].Text);
        // 命令消息本身不应产生 LLM 回复
        Assert.DoesNotContain("测试回复", bridge.Sent[1].Text);
    }

    [Fact]
    public async Task LogAnalysisRequest_RoutesToLogAgent()
    {
        var (runtime, bridge) = Make();
        var logMsg = Msg("分析日志\nERROR db down at X.java:1\n   at a.B(c)\n   at d.E(f)\n   at g.H(i)\n", mentioned: true);
        await runtime.OnMessageAsync(bridge, logMsg);
        Assert.Single(bridge.Sent);
        Assert.Contains("日志分析报告", bridge.Sent[0].Text);
    }

    [Fact]
    public async Task LongReport_SentInSegments()
    {
        // LLM 分析输出超 1600 字符 → 报告分段发送
        var longAnalysis = string.Join("", Enumerable.Range(0, 60).Select(i => $"根因{i}：某个组件在特定条件下失败，建议检查配置项{i}并重启服务。"));
        var logMsg = "分析日志\nERROR db down at X.java:1\n   at a.B(c)\n   at d.E(f)\n   at g.H(i)\n";
        var (runtime, bridge) = Make(logAnalysisReply: longAnalysis);
        await runtime.OnMessageAsync(bridge, Msg(logMsg, mentioned: true));
        Assert.True(bridge.Sent.Count >= 2, $"期望分段发送，实际 {bridge.Sent.Count} 段");
        Assert.StartsWith("(1/", bridge.Sent[0].Text);
    }
}
