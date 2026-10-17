using LogAgent.Knowledge;
using LogAgent.Routing;
using LogAgent.Shared;
using LogAgent.Shared.Chat;
using LogAgent.Shared.Configuration;

namespace LogAgent.Core;

/// <summary>
/// 学习 Agent（M4，规划 3.5 自学习管道）：
/// 路径A 用户回答沉淀：群内 "记住 XXX" → 提炼去噪 → 知识库候选区；
/// 路径B 网页学习："学习 https://…" → 抓取正文 → 提炼 → 候选区。
/// 候选经审核（人工 UI 或 AutoApprove）转正后进入 RAG 检索。
/// </summary>
public sealed class LearningAgent(ModelRouter router, KnowledgeStore knowledge, IWebFetcher web, KnowledgeConfig config)
{
    public event EventHandler<string>? LearningLog;

    /// <summary>用户告知的知识 → 提炼 → 候选区。返回给用户的确认消息。</summary>
    public async Task<string> LearnFromUserAsync(string factText, string senderName, CancellationToken ct = default)
    {
        if (factText.Trim().Length < 4)
            return "内容太短啦，记不住什么。请写完整一点，例如：记住 MySQL 8 连接串要加 allowPublicKeyRetrieval=true";

        var distilled = await DistillAsync($"群友 {senderName} 提供的信息：\n{factText}", ct);
        await knowledge.AddAsync(distilled, "user_reply", senderName, ct: ct);
        Log($"用户知识入库：{distilled[..Math.Min(50, distilled.Length)]}…");
        return config.AutoApprove
            ? "已记住 ✅（已自动生效）"
            : "已记下啦，等我确认审核后就会用上这条知识 📝";
    }

    /// <summary>网页学习：抓取 → 提炼 → 候选区。</summary>
    public async Task<string> LearnFromUrlAsync(string url, CancellationToken ct = default)
    {
        var text = await web.FetchTextAsync(url, ct);
        if (text.Trim().Length < 50)
            return "这个页面没什么正文可学（可能是纯脚本页面或需要登录）。";

        var distilled = await DistillAsync($"网页内容（{url}）：\n{text}", ct);
        await knowledge.AddAsync(distilled, "web", url, ct: ct);
        Log($"网页知识入库（来源 {url}）：{distilled[..Math.Min(50, distilled.Length)]}…");
        return $"已从网页提炼知识入库 📚{(!config.AutoApprove ? "（待审核后生效）" : "")}";
    }

    /// <summary>Reflection 式提炼：去噪、结构化为独立可检索知识（提示词见 <see cref="Prompts.KnowledgeDistiller"/>）。</summary>
    private async Task<string> DistillAsync(string raw, CancellationToken ct)
    {
        var result = await router.CompleteAsync(
            TaskType.Summary,
            [ChatTurn.System(Prompts.KnowledgeDistiller), ChatTurn.User(raw)],
            maxTokens: 400, temperature: 0.2, ct: ct);
        var distilled = result.Content.Trim();
        return distilled == "无有效知识" ? "（待审核）" + raw[..Math.Min(200, raw.Length)] : distilled;
    }

    private void Log(string line) => LearningLog?.Invoke(this, line);
}
