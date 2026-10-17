using LogAgent.Core;
using LogAgent.Knowledge;
using LogAgent.Memory;
using LogAgent.Routing;
using LogAgent.Shared.Configuration;
using LogAgent.Shared.Messaging;
using Xunit;

namespace LogAgent.Tests;

public class KnowledgeStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"la-kb-{Guid.NewGuid():N}.db");

    private KnowledgeStore Make(bool autoApprove = false)
    {
        var appConfig = new AppConfig
        {
            Providers = [new ProviderConfig { Id = "p1", Name = "P1", ChatModel = "m", Enabled = true }],
            Rules = [new RouterRule { TaskType = TaskType.Embedding, ProviderChain = ["p1"] }],
        };
        var router = new ModelRouter(appConfig, _ => new FakeClient("p1", true));
        var store = new KnowledgeStore(_dbPath, new KnowledgeConfig { AutoApprove = autoApprove }, router);
        store.EnsureCreated();
        return store;
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch (IOException) { }
    }

    [Fact]
    public async Task Add_UserSource_IsCandidate_ManualIsApproved()
    {
        var store = Make();
        await store.AddAsync("MySQL 8 连接需要 allowPublicKeyRetrieval=true", "user_reply", "柠枺");
        await store.AddAsync("手动知识", "manual");

        var candidates = store.List("candidate");
        var approved = store.List("approved");
        Assert.Single(candidates);
        Assert.Single(approved);
        Assert.Equal("user_reply", candidates[0].Source);
    }

    [Fact]
    public async Task Review_CandidateBecomesApproved()
    {
        var store = Make();
        var id = await store.AddAsync("候选知识内容", "user_reply");
        store.Review(id, approve: true);
        Assert.Empty(store.List("candidate"));
        Assert.Single(store.List("approved"));
    }

    [Fact]
    public async Task Search_Keyword_FindsApprovedOnly()
    {
        var store = Make();
        var id1 = await store.AddAsync("Nginx 502 通常是上游超时导致", "manual");
        var id2 = await store.AddAsync("Redis 连接数上限默认 10000", "manual");
        await store.AddAsync("Nginx 候选未审核知识", "user_reply"); // 候选，不应被检索到

        var hits = await store.SearchAsync("nginx 502 怎么回事", topK: 3);
        Assert.Single(hits);
        Assert.Equal(id1, hits[0].Item.Id);
        Assert.DoesNotContain(hits, h => h.Item.Id != id1 && h.Item.Id != id2);
        Assert.Contains("kw", hits[0].Via); // 无 embedding 配置 → 仅关键词
    }

    [Fact]
    public async Task Search_AutoApprove_CandidateImmediatelySearchable()
    {
        var store = Make(autoApprove: true);
        await store.AddAsync("Kafka 消费积压先查消费者组状态", "user_reply");
        var hits = await store.SearchAsync("kafka 积压", topK: 3);
        Assert.Single(hits);
    }

    [Fact]
    public void Delete_RemovesItem()
    {
        var store = Make();
        var id = store.AddAsync("将被删除的知识", "manual").Result;
        store.Delete(id);
        Assert.Empty(store.List());
    }
}

public class LearningAgentTests
{
    private static LearningAgent Make(KnowledgeStore store, bool autoApprove = false)
    {
        var appConfig = new AppConfig
        {
            Providers = [new ProviderConfig { Id = "p1", Name = "P1", ChatModel = "m", Enabled = true }],
            Rules = [new RouterRule { TaskType = TaskType.Summary, ProviderChain = ["p1"] }],
        };
        var router = new ModelRouter(appConfig, _ => new FakeClient("p1", true)
        {
            ReplySelector = input => input.Contains("知识提炼器") ? "提炼后的知识条目" : "测试回复",
        });
        return new LearningAgent(router, store, new FakeWebFetcher(), new KnowledgeConfig { AutoApprove = autoApprove });
    }

    private sealed class FakeWebFetcher : IWebFetcher
    {
        public Task<string> FetchTextAsync(string url, CancellationToken ct = default)
            => Task.FromResult("这是一篇关于数据库连接池配置的长文，详细讲解了 maxPoolSize 的取值原理、超时参数的最佳实践，以及常见报错的排查路径。");
    }

    [Fact]
    public async Task LearnFromUser_DistillsAndStoresCandidate()
    {
        var store = MakeStore();
        var agent = Make(store);
        var ack = await agent.LearnFromUserAsync("MySQL 8 连接报错要加 allowPublicKeyRetrieval=true", "柠枺");
        Assert.Contains("记下", ack);
        var candidate = Assert.Single(store.List("candidate"));
        Assert.Equal("提炼后的知识条目", candidate.Content);
        Assert.Equal("user_reply", candidate.Source);
        Assert.Equal("柠枺", candidate.SourceRef);
    }

    [Fact]
    public async Task LearnFromUrl_FetchesDistillsStores()
    {
        var store = MakeStore();
        var agent = Make(store);
        var ack = await agent.LearnFromUrlAsync("https://example.com/pool");
        Assert.Contains("网页", ack);
        var candidate = Assert.Single(store.List("candidate"));
        Assert.Equal("web", candidate.Source);
        Assert.Equal("https://example.com/pool", candidate.SourceRef);
    }

    private static KnowledgeStore MakeStore()
    {
        var path = Path.Combine(Path.GetTempPath(), $"la-learn-{Guid.NewGuid():N}.db");
        var appConfig = new AppConfig { Providers = [], Rules = [] };
        var store = new KnowledgeStore(path, new KnowledgeConfig(), new ModelRouter(appConfig));
        store.EnsureCreated();
        return store;
    }
}

public class LearnCommandRuntimeTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"la-rt4-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch (IOException) { }
    }

    private (AgentRuntime Runtime, RecordingBridge Bridge, KnowledgeStore Knowledge) Make()
    {
        var appConfig = new AppConfig
        {
            Providers = [new ProviderConfig { Id = "p1", Name = "P1", ChatModel = "m", Enabled = true }],
            Rules =
            [
                new RouterRule { TaskType = TaskType.Chat, ProviderChain = ["p1"] },
                new RouterRule { TaskType = TaskType.Summary, ProviderChain = ["p1"] },
            ],
        };
        var router = new ModelRouter(appConfig, _ => new FakeClient("p1", true)
        {
            ReplySelector = input => input.Contains("知识提炼器") ? "提炼结果" : "测试回复",
        });
        var db = new Database(_dbPath);
        db.EnsureCreated();
        var sessions = new SessionManager(new SessionStore(db), router, new AgentConfig());

        var kbPath = _dbPath + ".kb";
        var knowledge = new KnowledgeStore(kbPath, new KnowledgeConfig(), router);
        knowledge.EnsureCreated();
        var learning = new LearningAgent(router, knowledge, new FakeWebFetcher2(), new KnowledgeConfig());

        var runtime = new AgentRuntime(
            router,
            new GroupContextAgent(router, knowledge, new KnowledgeConfig { Enabled = true }),
            new LogAnalysisAgent(router),
            learning,
            sessions,
            new AgentConfig());
        var bridge = new RecordingBridge();
        return (runtime, bridge, knowledge);
    }

    private static BotMessage Msg(string text, bool mentioned = true) => new()
    {
        MessageId = "m1", ChatId = "777", ChatType = ChatType.Group,
        SenderId = "1", SenderName = "柠枺", Text = text, MentionedMe = mentioned,
    };

    [Fact]
    public async Task RememberCommand_StoresKnowledge_AndAcknowledges()
    {
        var (runtime, bridge, knowledge) = Make();
        await runtime.OnMessageAsync(bridge, Msg("记住：Nginx 502 大概率是上游超时"));
        Assert.Single(bridge.Sent);
        Assert.Contains("记下", bridge.Sent[0].Text);
        Assert.Single(knowledge.List("candidate"));
    }

    [Fact]
    public async Task LearnUrlCommand_FetchesAndStores()
    {
        var (runtime, bridge, knowledge) = Make();
        await runtime.OnMessageAsync(bridge, Msg("学习 https://example.com/post"));
        Assert.Single(bridge.Sent);
        Assert.Contains("网页", bridge.Sent[0].Text);
        Assert.Single(knowledge.List("candidate"));
    }

    [Fact]
    public async Task NormalQuestion_StillWorks()
    {
        var (runtime, bridge, _) = Make();
        await runtime.OnMessageAsync(bridge, Msg("普通问题"));
        Assert.Single(bridge.Sent);
        Assert.Equal("测试回复", bridge.Sent[0].Text);
    }
}

file sealed class FakeWebFetcher2 : IWebFetcher
{
    public Task<string> FetchTextAsync(string url, CancellationToken ct = default)
        => Task.FromResult("这是一篇足够长的技术文章正文，系统性地讲述了数据库连接池超时参数配置的来龙去脉、常见误区、线上事故复盘与最佳实践经验总结，内容详实可供提炼。");
}