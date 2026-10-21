using System.Text.Json.Serialization;

namespace LogAgent.Shared.Configuration;

/// <summary>任务类型——多模型分流的分类维度（对应规划 3.6）。</summary>
public enum TaskType
{
    /// <summary>群聊/私聊日常对话：优先低延迟。</summary>
    Chat,

    /// <summary>日志分析：优先质量（大模型）。</summary>
    LogAnalysis,

    /// <summary>深度推理/根因分析。</summary>
    DeepReasoning,

    /// <summary>会话摘要/压缩（L1/L2）：用便宜模型。</summary>
    Summary,

    /// <summary>向量化。</summary>
    Embedding,
}

/// <summary>一个 LLM provider 配置（OpenAI 兼容接口）。</summary>
public sealed class ProviderConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public string Endpoint { get; set; } = "https://api.openai.com/v1";
    public string ApiKey { get; set; } = "";
    public string ChatModel { get; set; } = "";
    public string? EmbeddingModel { get; set; }

    /// <summary>单次请求输出预算（max_tokens）。优先于任务级预算；模型元数据可自动填充。</summary>
    public int MaxTokens { get; set; } = 4096;

    /// <summary>上下文窗口 tokens（来自「获取模型列表」的元数据；0 = 未知）。</summary>
    public int ContextTokens { get; set; }

    /// <summary>思考模型（推理 token 与输出共享预算；请求按 MaxTokens 全量给出，避免"想完没得写"）。</summary>
    public bool ReasoningModel { get; set; }

    public double Temperature { get; set; } = 0.7;
    public int TimeoutSeconds { get; set; } = 120;
    public bool Enabled { get; set; } = true;
}

/// <summary>一条分流规则：任务类型 → provider 优先链（按顺序故障切换）。</summary>
public sealed class RouterRule
{
    public TaskType TaskType { get; set; } = TaskType.Chat;

    /// <summary>provider Id 有序列表；命中熔断/失败时依次向后切换。</summary>
    public List<string> ProviderChain { get; set; } = [];

    /// <summary>兜底：链上全部失败时使用的 provider Id（可空）。</summary>
    public string? FallbackProviderId { get; set; }
}

/// <summary>应用配置根（桌面/无头共用一份）。</summary>
public sealed class AppConfig
{
    public List<ProviderConfig> Providers { get; set; } = [];
    public List<RouterRule> Rules { get; set; } = [];
    public string DefaultProviderId { get; set; } = "";

    /// <summary>熔断阈值：连续失败次数。</summary>
    public int CircuitBreakerThreshold { get; set; } = 3;

    /// <summary>熔断恢复时长（秒）。</summary>
    public int CircuitBreakerRecoverySeconds { get; set; } = 60;

    /// <summary>
    /// 频道列表（OpenClaw 形态：一个频道 = 一种平台接入）。
    /// <para>这是新的唯一真相源；下面的 <see cref="OneBot11"/> / <see cref="OfficialBot"/>
    /// 仅为旧配置兼容保留，读取时经 <see cref="NormalizeChannels"/> 迁进频道。</para>
    /// <para>null = 配置文件里没有 Channels 字段（需要从旧字段迁移）；
    /// 空列表 = 用户明确移除了所有频道，不要再用旧字段复活。</para>
    /// </summary>
    public List<ChannelConfig>? Channels { get; set; }

    /// <summary>默认频道 ID（官方 Bot；新建配置时的首选）。</summary>
    public const string DefaultChannelId = "qq-official";

    // ── 旧版并列字段：只用于迁移，新代码请用 Channels ──
    [Obsolete("改用 Channels（频道模型，见 ChannelConfig）")]
    public OneBot11Config OneBot11 { get; set; } = new();
    [Obsolete("改用 Channels（频道模型，见 ChannelConfig）")]
    public OfficialBotConfig OfficialBot { get; set; } = new();

    public NapCatConfig NapCat { get; set; } = new();
    public AgentConfig Agent { get; set; } = new();
    public SandboxConfig Sandbox { get; set; } = new();
    public KnowledgeConfig Knowledge { get; set; } = new();

    /// <summary>技能（可扩展能力：工具调用/技能商店）。</summary>
    public SkillsConfig Skills { get; set; } = new();

    /// <summary>MCP 服务器（标准 Model Context Protocol 客户端）。</summary>
    public McpConfig Mcp { get; set; } = new();

    /// <summary>首次启动向导是否已完成（完成前每次启动都进入向导）。</summary>
    public bool WizardCompleted { get; set; } = false;

    /// <summary>启动应用后自动连接已启用的频道（官方 Bot 优先，其次 OneBot / NapCat）。</summary>
    public bool AutoStartBots { get; set; } = false;

    /// <summary>启动时最小化（适合开机自启场景）。</summary>
    public bool StartMinimized { get; set; } = false;

    public static AppConfig CreateDefault() => new()
    {
        Rules = Enum.GetValues<TaskType>()
            .Select(t => new RouterRule { TaskType = t, ProviderChain = [] })
            .ToList(),
        // 官方 Bot 优先：默认频道是官方 Bot，NapCat 作为并行的备用频道（均未启用）。
        Channels = [CreateOfficialChannel(), CreateOneBotChannel()],
    };

    /// <summary>默认官方 Bot 频道（QQ 开放平台，官方优先；未启用）。</summary>
    public static ChannelConfig CreateOfficialChannel() => new()
    {
        Id = DefaultChannelId,
        Type = "qq",
        Name = "QQ 官方 Bot",
        Enabled = false,
        Transport = ChannelTransport.Official,
        Priority = ChannelTransport.DefaultPriority(ChannelTransport.Official),
        MarkdownSupport = true,
        MergeMessages = true,
        Capabilities = new ChannelCapabilities
        {
            Forward = false, Merged = true, Markdown = true, GroupProactive = true,
        },
    };

    /// <summary>默认 OneBot11/NapCat 频道（未启用）。</summary>
    public static ChannelConfig CreateOneBotChannel() => new()
    {
        Id = "qq-onebot",
        Type = "qq",
        Name = "QQ（NapCat / OneBot11）",
        Enabled = false,
        Transport = ChannelTransport.OneBot11,
        Priority = ChannelTransport.DefaultPriority(ChannelTransport.OneBot11),
        MarkdownSupport = false,
        MergeMessages = false,
        OneBot11 = new OneBot11Config(),
        Capabilities = new ChannelCapabilities { Forward = true },
    };

    /// <summary>
    /// 把配置规整成频道模型：
    /// <list type="number">
    /// <item>没有 Channels 字段（旧配置）→ 从顶层 OneBot11 / OfficialBot 迁出两个频道；</item>
    /// <item>显式 <c>Channels: []</c> → 保持为空（用户主动移除，不复活旧字段）；</item>
    /// <item>补齐 ID / 名称 / 传输 / 优先级 / 能力等默认值。</item>
    /// </list>
    /// 幂等，可反复调用（<see cref="Configuration.AppConfigStore.Load"/> 里会调一次）。
    /// </summary>
    public void NormalizeChannels()
    {
        if (Channels is null)
        {
#pragma warning disable CS0618 // 迁移旧字段是刻意为之
            var legacyOneBot = OneBot11;
            var legacyOfficial = OfficialBot;
#pragma warning restore CS0618
            var official = CreateOfficialChannel();
            official.AdoptLegacy(legacyOfficial);
            var oneBot = CreateOneBotChannel();
            oneBot.AdoptLegacy(legacyOneBot);
            Channels = [official, oneBot];
        }

        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < Channels.Count; i++)
        {
            var c = Channels[i] ?? new ChannelConfig();
            Channels[i] = c;

            c.Transport = ChannelTransport.Normalize(c.Transport);
            c.Id = string.IsNullOrWhiteSpace(c.Id)
                ? ChannelTransport.Normalize(c.Transport) switch
                {
                    ChannelTransport.Official => DefaultChannelId,
                    ChannelTransport.OneBot11 => "qq-onebot",
                    var other => $"channel-{i + 1}-{other}",
                }
                : c.Id.Trim();
            if (!usedIds.Add(c.Id))
            {
                var baseId = c.Id;
                var suffix = 2;
                while (!usedIds.Add($"{baseId}-{suffix}")) suffix++;
                c.Id = $"{baseId}-{suffix}";
            }

            c.Type = string.IsNullOrWhiteSpace(c.Type) ? "qq" : c.Type.Trim();
            c.Name = string.IsNullOrWhiteSpace(c.Name)
                ? ChannelTransport.IsOfficial(c.Transport) ? "QQ 官方 Bot"
                : ChannelTransport.IsOneBot11(c.Transport) ? "QQ（NapCat / OneBot11）"
                : $"{c.Type}（{c.Transport}）"
                : c.Name.Trim();
            if (c.Priority == 0) c.Priority = ChannelTransport.DefaultPriority(c.Transport);

            c.OneBot11 ??= new OneBot11Config();
            c.Official ??= new OfficialBotConfig();
            c.Capabilities ??= new ChannelCapabilities();
            c.Capabilities.ApplyTransportDefaults(c.Transport);
            if (c.IsOneBot11) c.MarkdownSupport = false; // Markdown 仅官方 Bot 发送侧支持
            c.Capabilities.Markdown = c.IsOfficial && c.MarkdownSupport;
            if (c.MergeWindowMs <= 0) c.MergeWindowMs = 600;

            // 未提交版本曾把官方凭证放在频道内嵌 Official 里；读到就搬到频道级，统一出口。
            if (string.IsNullOrWhiteSpace(c.AppId) && string.IsNullOrWhiteSpace(c.AppSecret) &&
                string.IsNullOrEmpty(c.Token))
            {
                c.AppId = c.Official.AppId;
                c.AppSecret = c.Official.AppSecret;
                c.Sandbox ??= c.Official.Sandbox;
            }
        }
    }

    /// <summary>取优先级最高的启用频道（无则 null）。</summary>
    public ChannelConfig? ActiveChannel() => ActiveChannels().FirstOrDefault();

    /// <summary>
    /// 启用中的频道，按优先级从高到低（官方 Bot 默认先于 OneBot11）。
    /// 同优先级按配置顺序，保证可预测。
    /// </summary>
    public IReadOnlyList<ChannelConfig> ActiveChannels()
    {
        NormalizeChannels();
        return Channels!
            .Where(c => c.Enabled)
            .OrderByDescending(c => c.Priority)
            .ToList();
    }
}
