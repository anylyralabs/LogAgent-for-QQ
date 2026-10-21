using LogAgent.Knowledge;
using LogAgent.Memory;
using LogAgent.Routing;
using LogAgent.Shared;
using LogAgent.Shared.Chat;
using LogAgent.Shared.Configuration;
using LogAgent.Shared.Messaging;
using LogAgent.Skills;

namespace LogAgent.Core;

/// <summary>
/// 群上下文分析 Agent（M4：RAG 增强；技能扩展：工具调用循环）：
/// 上下文 = 系统提示 + 知识库检索命中（长文按命中位置截取）+ 近期 L1 摘要 + 当前窗口。
/// 命中知识时要求带引用编号回答；启用技能时把技能工具暴露给模型，模型可多轮调用后作答。
/// </summary>
public sealed class GroupContextAgent(
    ModelRouter router,
    KnowledgeStore? knowledge = null,
    KnowledgeConfig? knowledgeConfig = null,
    ISkillToolHost? skills = null,
    SkillsConfig? skillsConfig = null,
    ImageFetcher? imageFetcher = null)
{
    private const int HitContextBudget = 640;

    /// <summary>工具循环上限（防失控：超过后补一次无工具收尾）。文件分析常见「搜索→精读→再搜索」，给足轮次。</summary>
    private const int MaxToolRounds = 6;

    private readonly ImageFetcher _imageFetcher = imageFetcher ?? new ImageFetcher();

    public event EventHandler<string>? AgentLog;

    public Task<string> ReplyAsync(BotMessage message, SessionContext context, CancellationToken ct = default) =>
        ReplyAsync(message, context, markdown: false, ct);

    public async Task<string> ReplyAsync(BotMessage message, SessionContext context, bool markdown,
        CancellationToken ct = default)
    {
        var turns = new List<ChatTurn>
        {
            ChatTurn.System(Prompts.XiaoyanFor(markdown)),
            ChatTurn.System(Prompts.CurrentTimeLine()),
            ChatTurn.System(Prompts.ChatContextLine(message.ChatType, message.ChannelName)),
        };

        // RAG 检索（仅 approved 知识）
        if (knowledge is not null && knowledgeConfig is { Enabled: true })
        {
            var hits = await knowledge.SearchAsync(message.Text, knowledgeConfig.RagTopK, ct);
            if (hits.Count > 0)
            {
                var probes = KnowledgeText.QueryTokens(message.Text);
                var block = string.Join("\n", hits.Select((h, i) => FormatHit(h, i + 1, probes)));
                turns.Add(ChatTurn.System(Prompts.KnowledgeRefIntro + "\n" + block));
            }
        }

        if (context.RecentSummaries.Count > 0)
        {
            turns.Add(ChatTurn.System(Prompts.RecentTopicsIntro + "\n" +
                                      string.Join("\n---\n", context.RecentSummaries.Select((s, i) => $"[{i + 1}] {s}"))));
        }

        // 指令型技能（SKILL.md）：技能清单进系统提示，模型按需 read_skill 读取全文
        if (skills is not null && skillsConfig is { Enabled: true })
        {
            var promptBlock = skills.BuildPromptSkillsBlock(skillsConfig);
            if (!string.IsNullOrEmpty(promptBlock))
                turns.Add(ChatTurn.System(promptBlock));
        }

        turns.AddRange(context.WindowTurns);

        // 当前消息带图片：下载转 data URI 附到当前 user turn（多模态，模型可读图）
        if (message.ImageUrls is { Count: > 0 })
        {
            var notes = new List<string>();
            var dataUris = new List<string>();
            foreach (var url in message.ImageUrls.Take(ImageFetcher.MaxImages))
            {
                var (dataUri, error) = await _imageFetcher.FetchAsDataUriAsync(url, ct);
                if (dataUri is not null) dataUris.Add(dataUri);
                else notes.Add($"【图片未能读取：{error}】");
            }
            if (dataUris.Count > 0)
            {
                // 会话窗口最后一条 user 即当前消息（SessionManager.OnIncomingAsync 追加）；仅附加图片不改文本
                var lastUser = turns.FindLastIndex(t => t.Role == "user");
                if (lastUser >= 0)
                    turns[lastUser] = turns[lastUser] with { ImageDataUrls = dataUris };
                else
                    turns.Add(ChatTurn.UserWithImages(message.Text, dataUris));
                Log($"图片已附到消息（{dataUris.Count} 张）");
            }
            if (message.ImageUrls.Count > ImageFetcher.MaxImages)
                notes.Add($"【图片较多，仅读取前 {ImageFetcher.MaxImages} 张】");
            foreach (var note in notes)
                turns.Add(ChatTurn.System(note));
        }

        // 技能工具（启用时暴露给模型，支持多轮调用）
        var tools = skills is not null && skillsConfig is { Enabled: true }
            ? skills.BuildToolDefinitions(skillsConfig)
            : [];
        if (tools.Count > 0)
            Log($"技能工具已装载：{string.Join("、", tools.Select(t => t.Name))}");

        for (var round = 0; round < MaxToolRounds; round++)
        {
            var result = await router.CompleteAsync(TaskType.Chat, turns, ct: ct,
                tools: tools.Count > 0 ? tools : null);
            if (result.ToolCalls is not { Count: > 0 } calls)
                return ToolMarkup.Strip(result.Content);

            turns.Add(ChatTurn.AssistantWithTools(result.Content, calls));
            foreach (var call in calls)
            {
                Log($"调用技能工具：{call.Name}（{Truncate(call.Arguments, 120)}）");
                var output = skills is null || skillsConfig is null
                    ? "错误：技能系统未启用"
                    : await skills.ExecuteToolAsync(call.Name, call.Arguments, skillsConfig, ct);
                turns.Add(ChatTurn.Tool(call.Id, output));
                Log($"{call.Name} 返回 {output.Length} 字符");
            }
        }

        // 超过轮数上限：去掉工具强制收尾（避免继续调用）
        Log("工具循环达到上限，收尾作答");
        turns.Add(ChatTurn.User("（轮次已用尽）请不要再调用工具，直接给出最终回答。"));
        var final = await router.CompleteAsync(TaskType.Chat, turns, ct: ct);
        return ToolMarkup.Strip(final.Content);
    }

    private static string FormatHit(KnowledgeHit hit, int index, IReadOnlyList<string> probes)
    {
        var body = KnowledgeText.ClipAround(hit.Item.Content, probes, HitContextBudget);
        var title = string.IsNullOrEmpty(hit.Item.Title) ? "" : $"【{hit.Item.Title}】";
        return $"[{index}] {title}{body}（来源：{hit.Item.DescribeSource()}）";
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    private void Log(string line) => AgentLog?.Invoke(this, line);
}
