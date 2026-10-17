namespace LogAgent.Shared.Configuration;

/// <summary>Agent 行为与触发规则配置（规划 3.3 / M2）。</summary>
public sealed class AgentConfig
{
    /// <summary>群聊是否需要 @ 机器人才响应（私聊总是响应）。</summary>
    public bool NeedAtInGroup { get; set; } = true;

    /// <summary>额外触发关键词（即使未 @ 也响应）。</summary>
    public List<string> TriggerKeywords { get; set; } = [];

    /// <summary>会话超时切断（分钟，静默超过即切段）。</summary>
    public int SessionIdleTimeoutMinutes { get; set; } = 30;

    /// <summary>会话容量切断：token 估算上限。</summary>
    public int SessionMaxTokens { get; set; } = 8000;

    /// <summary>会话容量切断：消息数上限。</summary>
    public int SessionMaxMessages { get; set; } = 200;

    /// <summary>切段时是否生成 L1 摘要（走 Summary 分流，便宜模型）。</summary>
    public bool SummaryOnCut { get; set; } = true;
}
