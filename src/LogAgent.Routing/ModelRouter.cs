using System.Collections.Concurrent;
using LogAgent.Shared.Chat;
using LogAgent.Shared.Configuration;

namespace LogAgent.Routing;

/// <summary>路由计量事件（成本看板数据源）。</summary>
public sealed record RouterMeterEvent(
    TaskType TaskType,
    string ProviderId,
    string Model,
    bool Success,
    int PromptTokens,
    int CompletionTokens,
    TimeSpan Latency,
    string? Error = null);

/// <summary>单个 provider 的熔断状态。</summary>
public sealed class CircuitState
{
    public int ConsecutiveFailures { get; set; }
    public DateTimeOffset? OpenedAt { get; set; }
    public bool IsOpen(DateTimeOffset now, int recoverySeconds)
        => OpenedAt is { } opened && (now - opened).TotalSeconds < recoverySeconds;
}

/// <summary>
/// 多模型分流路由器 v1：任务分类（规则查表）→ provider 优先链 → 熔断跳过 → 失败切换 → 统一计量。
/// 客户端通过工厂注入，便于测试替换（规划 3.6）。
/// </summary>
public sealed class ModelRouter
{
    private readonly Func<ProviderConfig, IChatModelClient> _clientFactory;
    private readonly ConcurrentDictionary<string, CircuitState> _circuits = new();
    private readonly ConcurrentDictionary<string, IChatModelClient> _clients = new();

    public AppConfig Config { get; }
    public event EventHandler<RouterMeterEvent>? Metered;

    public ModelRouter(AppConfig config, Func<ProviderConfig, IChatModelClient>? clientFactory = null)
    {
        Config = config;
        _clientFactory = clientFactory ?? (p => new OpenAiCompatClient(p));
    }

    /// <summary>解析任务类型的 provider 尝试顺序（含兜底）。</summary>
    public IReadOnlyList<string> ResolveChain(TaskType taskType)
    {
        var rule = Config.Rules.FirstOrDefault(r => r.TaskType == taskType);
        var chain = new List<string>(rule?.ProviderChain ?? []);
        if (rule?.FallbackProviderId is { } fb && !chain.Contains(fb)) chain.Add(fb);
        if (Config.DefaultProviderId is { Length: > 0 } def && !chain.Contains(def)) chain.Add(def);
        // 过滤不存在/禁用的 provider，保持顺序
        var known = Config.Providers.Where(p => p.Enabled).Select(p => p.Id).ToHashSet();
        return chain.Where(known.Contains).ToList();
    }

    /// <summary>
    /// 按任务类型执行补全：沿优先链尝试，熔断打开的跳过，失败切换下一个。
    /// 输出预算（max_tokens）规则：思考模型按 provider.MaxTokens 全量给出（推理 token 与输出共享预算，
    /// 小预算会把回复"想没了"）；普通模型取 任务需求量 与 provider 预算 的较小值；任务未指定（0）时用 provider 预算。
    /// </summary>
    public async Task<ChatResult> CompleteAsync(TaskType taskType, IReadOnlyList<ChatTurn> messages,
        int maxTokens = 0, double temperature = 0.7, CancellationToken ct = default,
        IReadOnlyList<ChatToolDefinition>? tools = null)
    {
        var chain = ResolveChain(taskType);
        if (chain.Count == 0)
            throw new InvalidOperationException(
                $"尚未配置模型——请打开「模型路由」页添加 Provider 并保存（任务类型：{taskType}）");

        Exception? lastError = null;
        foreach (var providerId in chain)
        {
            var provider = Config.Providers.First(p => p.Id == providerId);
            var state = _circuits.GetOrAdd(providerId, _ => new CircuitState());
            var now = DateTimeOffset.UtcNow;
            if (state.IsOpen(now, Config.CircuitBreakerRecoverySeconds))
                continue; // 熔断中，跳过

            var budget = Math.Max(1, provider.MaxTokens);
            var effectiveMax = provider.ReasoningModel
                ? budget
                : maxTokens > 0
                    ? Math.Min(maxTokens, budget)
                    : budget;

            var client = _clients.GetOrAdd(providerId, id => _clientFactory(provider));
            try
            {
                var result = await client.CompleteAsync(
                    new ChatRequest(messages, effectiveMax, temperature, tools), ct);
                state.ConsecutiveFailures = 0;
                state.OpenedAt = null;
                Metered?.Invoke(this, new RouterMeterEvent(taskType, providerId, result.Model,
                    true, result.PromptTokens, result.CompletionTokens, result.Latency));
                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
                state.ConsecutiveFailures++;
                if (state.ConsecutiveFailures >= Config.CircuitBreakerThreshold)
                    state.OpenedAt = DateTimeOffset.UtcNow;
                Metered?.Invoke(this, new RouterMeterEvent(taskType, providerId, provider.ChatModel,
                    false, 0, 0, TimeSpan.Zero, ex.Message));
            }
        }
        throw new InvalidOperationException($"任务 {taskType} 的全部 provider 均失败，最后错误：{lastError?.Message}", lastError);
    }

    /// <summary>文本向量化（Embedding 分流；未配置则抛异常，调用方降级为纯关键词检索）。</summary>
    public async Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
    {
        var chain = ResolveChain(TaskType.Embedding);
        if (chain.Count == 0) return null;
        var provider = Config.Providers.First(p => p.Id == chain[0]);
        if (string.IsNullOrEmpty(provider.EmbeddingModel)) return null;
        var client = new OpenAiEmbeddingsClient(provider);
        try
        {
            var vector = await client.EmbedAsync(text, ct);
            Metered?.Invoke(this, new RouterMeterEvent(TaskType.Embedding, provider.Id,
                provider.EmbeddingModel!, true, 0, 0, TimeSpan.Zero));
            return vector;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Metered?.Invoke(this, new RouterMeterEvent(TaskType.Embedding, provider.Id,
                provider.EmbeddingModel!, false, 0, 0, TimeSpan.Zero, ex.Message));
            return null; // 向量失败降级，不阻断主流程
        }
    }

    /// <summary>查询 provider 熔断状态（UI 看板用）。</summary>
    public IReadOnlyDictionary<string, CircuitState> GetCircuitSnapshot() => _circuits;
}
