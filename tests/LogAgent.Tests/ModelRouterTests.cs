using LogAgent.Routing;
using LogAgent.Shared.Chat;
using LogAgent.Shared.Configuration;
using Xunit;

namespace LogAgent.Tests;

/// <summary>可控的假客户端：按 providerId 决定成功/失败；可按输入内容选择回复。</summary>
sealed class FakeClient(string providerId, bool succeed, string reply = "ok") : IChatModelClient
{
    public string ProviderId { get; } = providerId;
    public int Calls { get; private set; }
    public int LastMaxTokens { get; private set; }

    /// <summary>最近一次请求（供集成测试检查多模态等载荷）。</summary>
    public ChatRequest? LastRequest { get; private set; }

    public Action<string>? OnInput { get; init; }
    public Func<string, string>? ReplySelector { get; init; }

    /// <summary>整结果覆盖（工具调用场景：按请求返回自定义 ChatResult）。</summary>
    public Func<ChatRequest, ChatResult?>? ResultOverride { get; init; }

    public Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken ct = default)
    {
        Calls++;
        LastMaxTokens = request.MaxTokens;
        LastRequest = request;
        if (ResultOverride?.Invoke(request) is { } custom)
            return Task.FromResult(custom);
        var input = string.Join("\n", request.Messages.Select(m => m.Content));
        OnInput?.Invoke(input);
        var text = ReplySelector?.Invoke(input) ?? reply;
        return succeed
            ? Task.FromResult(new ChatResult(text, providerId, "fake-model", 10, 5, TimeSpan.FromMilliseconds(1)))
            : throw new HttpRequestException($"fake failure {providerId}");
    }
}

public class ModelRouterTests
{
    private static (AppConfig Config, Dictionary<string, FakeClient> Clients) Setup(
        bool firstOk = false, bool secondOk = true)
    {
        var p1 = new ProviderConfig { Id = "p1", Name = "P1", ChatModel = "m1", Enabled = true };
        var p2 = new ProviderConfig { Id = "p2", Name = "P2", ChatModel = "m2", Enabled = true };
        var disabled = new ProviderConfig { Id = "pd", Name = "PD", ChatModel = "m3", Enabled = false };
        var config = new AppConfig
        {
            Providers = [p1, p2, disabled],
            Rules = [new RouterRule { TaskType = TaskType.Chat, ProviderChain = ["p1", "p2"] }],
            DefaultProviderId = "p2",
        };
        var clients = new Dictionary<string, FakeClient>
        {
            ["p1"] = new FakeClient("p1", firstOk),
            ["p2"] = new FakeClient("p2", secondOk),
        };
        return (config, clients);
    }

    private static ModelRouter MakeRouter(AppConfig config, Dictionary<string, FakeClient> clients)
        => new(config, p => clients[p.Id]);

    [Fact]
    public void ResolveChain_FollowsRuleOrder_AndSkipsDisabled()
    {
        var (config, _) = Setup();
        var router = MakeRouter(config, []);
        var chain = router.ResolveChain(TaskType.Chat);
        // p1 → p2（规则链）→ p2 不重复（默认），pd 已禁用被过滤
        Assert.Equal(["p1", "p2"], chain);
    }

    [Fact]
    public void ResolveChain_EmptyRuleFallsBackToDefault()
    {
        var (config, _) = Setup();
        config.Rules[0].ProviderChain = [];
        var router = MakeRouter(config, []);
        Assert.Equal(["p2"], router.ResolveChain(TaskType.Chat));
    }

    [Fact]
    public async Task CompleteAsync_FailsOverToNextProvider()
    {
        var (config, clients) = Setup(firstOk: false, secondOk: true);
        var router = MakeRouter(config, clients);
        var result = await router.CompleteAsync(TaskType.Chat, [ChatTurn.User("hi")]);
        Assert.Equal("p2", result.ProviderId);
        Assert.Equal(1, clients["p1"].Calls);
        Assert.Equal(1, clients["p2"].Calls);
    }

    [Fact]
    public async Task CompleteAsync_ThrowsWhenAllFail()
    {
        var (config, clients) = Setup(firstOk: false, secondOk: false);
        var router = MakeRouter(config, clients);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => router.CompleteAsync(TaskType.Chat, [ChatTurn.User("hi")]));
    }

    [Fact]
    public async Task CircuitBreaker_OpensAfterConsecutiveFailures()
    {
        var (config, clients) = Setup(firstOk: false, secondOk: false);
        config.CircuitBreakerThreshold = 2;
        var router = MakeRouter(config, clients);

        await Assert.ThrowsAsync<InvalidOperationException>(() => router.CompleteAsync(TaskType.Chat, [ChatTurn.User("a")]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => router.CompleteAsync(TaskType.Chat, [ChatTurn.User("b")]));
        var callsAfterTwoRounds = clients["p1"].Calls; // p1 已失败 2 次 → 熔断打开

        await Assert.ThrowsAsync<InvalidOperationException>(() => router.CompleteAsync(TaskType.Chat, [ChatTurn.User("c")]));
        // 熔断打开后 p1 不再被调用
        Assert.Equal(callsAfterTwoRounds, clients["p1"].Calls);
    }

    [Fact]
    public async Task MeteredEvent_FiresOnSuccessAndFailure()
    {
        var (config, clients) = Setup(firstOk: false, secondOk: true);
        var router = MakeRouter(config, clients);
        var events = new List<RouterMeterEvent>();
        router.Metered += (_, e) => events.Add(e);

        await router.CompleteAsync(TaskType.Chat, [ChatTurn.User("hi")]);

        Assert.Equal(2, events.Count);
        Assert.False(events[0].Success);
        Assert.True(events[1].Success);
    }

    [Fact]
    public async Task OutputBudget_NormalModel_TakesMinOfTaskAndProvider()
    {
        var (config, clients) = Setup(firstOk: true);
        config.Providers[0].MaxTokens = 8192;
        var router = MakeRouter(config, clients);

        await router.CompleteAsync(TaskType.Chat, [ChatTurn.User("hi")], maxTokens: 2000); // 任务预算更小 → 取 2000
        Assert.Equal(2000, clients["p1"].LastMaxTokens);

        await router.CompleteAsync(TaskType.Chat, [ChatTurn.User("hi")]); // 未指定 → 用 provider 预算
        Assert.Equal(8192, clients["p1"].LastMaxTokens);
    }

    [Fact]
    public async Task OutputBudget_ReasoningModel_UsesFullProviderBudget()
    {
        var (config, clients) = Setup(firstOk: true);
        config.Providers[0].MaxTokens = 64000;
        config.Providers[0].ReasoningModel = true; // 思考模型：推理 token 与输出共享预算
        var router = MakeRouter(config, clients);

        await router.CompleteAsync(TaskType.Chat, [ChatTurn.User("hi")], maxTokens: 4096);
        Assert.Equal(64000, clients["p1"].LastMaxTokens); // 小预算会被"想没了"，必须全量
    }
}
