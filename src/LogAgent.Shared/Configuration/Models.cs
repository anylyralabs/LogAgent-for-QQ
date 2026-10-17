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

    public OneBot11Config OneBot11 { get; set; } = new();
    public NapCatConfig NapCat { get; set; } = new();
    public OfficialBotConfig OfficialBot { get; set; } = new();
    public AgentConfig Agent { get; set; } = new();
    public SandboxConfig Sandbox { get; set; } = new();
    public KnowledgeConfig Knowledge { get; set; } = new();

    /// <summary>技能（可扩展能力：工具调用/技能商店）。</summary>
    public SkillsConfig Skills { get; set; } = new();

    /// <summary>MCP 服务器（标准 Model Context Protocol 客户端）。</summary>
    public McpConfig Mcp { get; set; } = new();

    /// <summary>首次启动向导是否已完成（完成前每次启动都进入向导）。</summary>
    public bool WizardCompleted { get; set; } = false;

    /// <summary>启动应用后自动连接已启用的 Bot（OneBot 服务 + 官方 Bot）。</summary>
    public bool AutoStartBots { get; set; } = false;

    /// <summary>启动时最小化（适合开机自启场景）。</summary>
    public bool StartMinimized { get; set; } = false;

    public static AppConfig CreateDefault() => new()
    {
        Rules = Enum.GetValues<TaskType>()
            .Select(t => new RouterRule { TaskType = t, ProviderChain = [] })
            .ToList(),
    };
}
