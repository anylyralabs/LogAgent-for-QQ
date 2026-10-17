namespace LogAgent.Shared.Configuration;

/// <summary>知识库配置（规划 3.4）。</summary>
public sealed class KnowledgeConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>RAG 检索注入条数。</summary>
    public int RagTopK { get; set; } = 3;

    /// <summary>学习命令产生的候选知识是否自动转正（默认人工审核）。</summary>
    public bool AutoApprove { get; set; } = false;
}
