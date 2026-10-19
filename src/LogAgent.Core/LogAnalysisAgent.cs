using System.Text.RegularExpressions;
using LogAgent.Knowledge;
using LogAgent.Routing;
using LogAgent.Shared;
using LogAgent.Shared.Chat;
using LogAgent.Shared.Configuration;
using LogAgent.Skills;

namespace LogAgent.Core;

/// <summary>
/// 日志分析 Agent（M3：Plan-Execute + Reflection，规划 3.2 / 5 节流程）：
/// 【文本模式】（粘贴日志）本地解析统计 → 降采样材料 → 知识库相似案例 → 根因推断 → Reflection → 报告
/// 【文件模式】（QQ 文件/抓取日志已落盘）消息里带「本机路径：…」时，模型用内置文件工具
/// （search_file 搜索 / read_file 精读 / run_shell 命令）查看完整文件——中间内容不省略，自己决定看哪段。
/// </summary>
public sealed class LogAnalysisAgent(
    ModelRouter router,
    KnowledgeStore? knowledge = null,
    KnowledgeConfig? knowledgeConfig = null,
    ISkillToolHost? skills = null,
    SkillsConfig? skillsConfig = null,
    string? filesDir = null)
{
    private const int CaseTopK = 3;
    private const int CaseContextBudget = 900;
    private const int FileToolRounds = 7;
    private const int ReviewNoteChars = 500;

    public event EventHandler<string>? AnalysisLog;

    public async Task<string> AnalyzeAsync(string logText, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(logText))
            return "这段内容没有可识别的行，看起来不是日志。把日志文本直接发给我试试～";

        // 文件模式：消息引用了本机已落盘的日志文件（富化环节写入的「本机路径」）→ 用工具查看全文
        if (TryExtractStagedFilePath(logText, out var filePath))
            return await AnalyzeFileAsync(logText, filePath, ct);

        return await AnalyzeTextAsync(logText, ct);
    }

    // ---------- 文件模式：模型用工具查看完整文件（中间不省略） ----------

    private async Task<string> AnalyzeFileAsync(string requestText, string filePath, CancellationToken ct)
    {
        var fileName = Path.GetFileName(filePath);
        long sizeBytes;
        try { sizeBytes = new FileInfo(filePath).Length; }
        catch (IOException) { sizeBytes = 0; }
        var sizeText = FormatSize(sizeBytes);
        var lines = CountLines(filePath);
        Log($"文件模式：{fileName}（{sizeText}，约 {lines} 行）");

        var intro = $"""
            用户的请求：{Shorten(requestText, 800)}

            目标日志文件：{fileName}（本机路径 {filePath}；{sizeText}，约 {lines} 行）
            注意：文件内容不随本消息提供——请用工具查看它的任意部分（中间内容都在）：
            · search_file：搜索错误标记定位区段（Exception / ERROR / FATAL / SIGSEGV / crash / 退出码 / Mod 等）
            · read_file：按行区间精读命中区域（offset / limit，带行号）
            · run_shell：执行命令交叉验证（工作目录即该文件所在目录）
            建议流程：先搜索错误标记 → 精读相关区段（含上下文）→ 必要时命令验证 → 再完成根因分析。
            """;

        var toolsAvailable = skills is not null && skillsConfig is { Enabled: true };
        if (!toolsAvailable)
        {
            // 无工具（理论兜底）：取文件头样本走文本统计
            var sample = ReadFileSample(filePath, 200 * 1024);
            Log("工具未启用，退化为文件头样本分析");
            return await AnalyzeTextAsync(sample, ct);
        }

        var turns = new List<ChatTurn>
        {
            ChatTurn.System(Prompts.LogAnalysisRole),
            ChatTurn.System(Prompts.CurrentTimeLine()),
            ChatTurn.User(intro),
        };
        var tools = skills!.BuildToolDefinitions(skillsConfig!);
        var analysisText = "";
        var answered = false;
        for (var round = 0; round < FileToolRounds; round++)
        {
            var result = await router.CompleteAsync(TaskType.LogAnalysis, turns, ct: ct,
                tools: tools.Count > 0 ? tools : null);
            if (result.ToolCalls is not { Count: > 0 } calls)
            {
                analysisText = result.Content;
                answered = true;
                break;
            }
            turns.Add(ChatTurn.AssistantWithTools(result.Content, calls));
            foreach (var call in calls)
            {
                Log($"调用工具：{call.Name}（{Shorten(call.Arguments, 100)}）");
                var output = await skills.ExecuteToolAsync(call.Name, call.Arguments, skillsConfig!, ct);
                turns.Add(ChatTurn.Tool(call.Id, output));
                Log($"{call.Name} 返回 {output.Length} 字符");
            }
        }
        if (!answered)
        {
            Log("工具循环达到上限，收尾作答");
            turns.Add(ChatTurn.User("（工具轮次已用尽）请不要再调用工具，直接基于已获取的信息给出最终根因分析结论。"));
            var final = await router.CompleteAsync(TaskType.LogAnalysis, turns, ct: ct);
            analysisText = final.Content;
        }
        analysisText = ToolMarkup.Strip(analysisText).Trim();

        // Reflection 自检（同文本模式）
        var reflection = await router.CompleteAsync(
            TaskType.DeepReasoning,
            [
                ChatTurn.System(Prompts.LogAnalysisReview),
                ChatTurn.User($"日志文件概况：\n{intro}\n\n待审结论：\n{analysisText}"),
            ], maxTokens: 500, temperature: 0.1, ct: ct);
        var passed = reflection.Content.TrimStart().StartsWith("PASS", StringComparison.OrdinalIgnoreCase);
        Log(passed ? "Reflection 通过" : "Reflection 未通过，将附注风险提示");
        var reviewNote = passed
            ? ""
            : $"\n\n【自检提示】（结论可能有偏差，仅供参考）\n{Shorten(ToolMarkup.Strip(reflection.Content).Trim(), ReviewNoteChars)}";

        return $"""
            📋 日志分析报告（文件：{fileName}）

            · 文件大小 {sizeText}，约 {lines} 行

            {analysisText}{reviewNote}
            """;
    }

    private static readonly Regex StagedPathPattern = new(@"本机路径：([^\r\n]+)", RegexOptions.Compiled);

    /// <summary>从消息文本中提取富化环节写入的「本机路径」（须存在且位于 files 暂存目录内）。</summary>
    private bool TryExtractStagedFilePath(string text, out string path)
    {
        path = "";
        var match = StagedPathPattern.Match(text);
        if (!match.Success) return false;
        var candidate = match.Groups[1].Value.Trim().Trim('"');
        if (candidate.Length == 0) return false;
        try
        {
            if (!File.Exists(candidate)) return false;
            var root = Path.GetFullPath(filesDir ?? AppConfigStore.GetFilesDirectory());
            var full = Path.GetFullPath(candidate);
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
            path = full;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }
    }

    private static long CountLines(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var buffer = new byte[65536];
            long count = 0;
            int read;
            while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
                count += buffer.AsSpan(0, read).Count((byte)'\n');
            return count + 1;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static string ReadFileSample(string path, int maxBytes)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var take = (int)Math.Min(fs.Length, maxBytes);
            var buffer = new byte[take];
            var read = 0;
            while (read < take)
            {
                var n = fs.Read(buffer, read, take - read);
                if (n == 0) break;
                read += n;
            }
            return System.Text.Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch (IOException)
        {
            return "";
        }
    }

    // ---------- 文本模式：粘贴日志的本地统计流程 ----------

    private async Task<string> AnalyzeTextAsync(string logText, CancellationToken ct)
    {
        // ---- Step 1: 本地统计 ----
        if (string.IsNullOrWhiteSpace(logText))
            return "这段内容没有可识别的行，看起来不是日志。把日志文本直接发给我试试～";
        var entries = LogParser.Parse(logText);
        var stats = LogStats.Compute(entries);
        Log($"解析完成：{stats.TotalLines} 行，错误类 {stats.ErrorLike} 条，错误簇 {stats.TopErrors.Count} 个，堆栈 {stats.StackTraces.Count} 段");

        if (stats.TotalLines == 0)
            return "这段内容没有可识别的行，看起来不是日志。把日志文本直接发给我试试～";
        if (stats.ErrorLike == 0 && stats.TopErrors.Count == 0)
            return $"日志共 {stats.TotalLines} 行，没有发现 ERROR/FATAL 级别或异常堆栈，看起来运行正常 ✅\n级别分布：{DescribeLevels(stats)}";

        // ---- Step 2: 降采样材料 ----
        var material = BuildMaterial(stats, maxChars: 6000);
        Log($"送入模型的材料约 {material.Length} 字符（已降采样）");

        // ---- Step 2.5: 知识库相似案例（对照判断命中）----
        var cases = await SearchCasesAsync(stats, ct);

        // ---- Step 3: 根因推断 ----
        var analysis = await router.CompleteAsync(
            TaskType.LogAnalysis,
            [ChatTurn.System(Prompts.LogAnalysisRole), ChatTurn.System(Prompts.CurrentTimeLine()), ChatTurn.User(material + cases)],
            maxTokens: 2000, temperature: 0.3, ct: ct);
        Log($"根因推断完成（{analysis.PromptTokens}+{analysis.CompletionTokens} tokens）");

        // ---- Step 4: Reflection 自检 ----
        var reflection = await router.CompleteAsync(
            TaskType.DeepReasoning,
            [
                ChatTurn.System(Prompts.LogAnalysisReview),
                ChatTurn.User($"日志材料：\n{material}{cases}\n\n待审结论：\n{analysis.Content}"),
            ], maxTokens: 500, temperature: 0.1, ct: ct);

        var passed = reflection.Content.TrimStart().StartsWith("PASS", StringComparison.OrdinalIgnoreCase);
        Log(passed ? "Reflection 通过" : "Reflection 未通过，将附注风险提示");
        var reviewNote = passed
            ? ""
            : $"\n\n【自检提示】（结论可能有偏差，仅供参考）\n{Shorten(ToolMarkup.Strip(reflection.Content).Trim(), ReviewNoteChars)}";

        // ---- Step 5: 报告 ----
        return $"""
            📋 日志分析报告

            · 总行数 {stats.TotalLines}，错误类 {stats.ErrorLike} 条
            · 级别分布：{DescribeLevels(stats)}
            · 错误簇 Top{Math.Min(3, stats.TopErrors.Count)}：{DescribeTopErrors(stats)}

            {analysis.Content}{reviewNote}
            """;
    }

    /// <summary>检索知识库相似案例（对齐 LogShare：报错签名命中率最高，用错误模板 + 首段堆栈作查询）。</summary>
    private async Task<string> SearchCasesAsync(LogStats.Stats stats, CancellationToken ct)
    {
        if (knowledge is null || knowledgeConfig is not { Enabled: true }) return "";
        try
        {
            var query = BuildCaseQuery(stats);
            if (query.Length == 0) return "";
            var cases = await knowledge.SearchAsync(query, CaseTopK, ct);
            if (cases.Count == 0)
            {
                Log("知识库未命中相似案例");
                return "";
            }
            var probes = KnowledgeText.QueryTokens(query);
            var block = string.Join("\n", cases.Select((h, i) =>
            {
                var title = string.IsNullOrEmpty(h.Item.Title) ? "" : $"【{h.Item.Title}】";
                var body = KnowledgeText.ClipAround(h.Item.Content, probes, CaseContextBudget);
                return $"[K{i + 1}] {title}{body}（来源：{h.Item.DescribeSource()}）";
            }));
            Log($"知识库相似案例：{cases.Count} 条（{string.Join("、", cases.Select(c => "#" + c.Item.Id))}）");
            return "\n\n知识库相似案例（对照判断是否命中，命中请引用编号）：\n" + block;
        }
        catch (Exception ex)
        {
            Log($"知识库检索失败（跳过案例对照）：{ex.Message}");
            return "";
        }
    }

    private static string BuildCaseQuery(LogStats.Stats stats)
    {
        var parts = new List<string>();
        parts.AddRange(stats.TopErrors.Take(3).Select(c => Shorten(c.Template, 120)));
        if (stats.StackTraces.Count > 0) parts.Add(Shorten(stats.StackTraces[0], 200));
        return string.Join("\n", parts);
    }

    private static string BuildMaterial(LogStats.Stats stats, int maxChars)
    {
        var parts = new List<string>();
        parts.Add($"级别统计：{DescribeLevels(stats)}");
        foreach (var cluster in stats.TopErrors.Take(6))
            parts.Add($"错误簇（×{cluster.Count}）：{cluster.Template}\n  样本：{string.Join(" | ", cluster.Samples)}");
        foreach (var (stack, i) in stats.StackTraces.Select((s, i) => (s, i)))
            parts.Add($"堆栈#{i + 1}：\n{stack}");
        var material = string.Join("\n\n", parts);
        return material.Length > maxChars ? material[..maxChars] + "\n…(材料截断)" : material;
    }

    private static string DescribeLevels(LogStats.Stats stats) =>
        stats.LevelCounts.Count == 0
            ? "未识别出标准级别标记"
            : string.Join("、", stats.LevelCounts.OrderBy(kv => -kv.Value).Select(kv => $"{kv.Key}={kv.Value}"));

    private static string DescribeTopErrors(LogStats.Stats stats) =>
        stats.TopErrors.Count == 0
            ? "无"
            : string.Join("；", stats.TopErrors.Take(3).Select(c => $"「{Shorten(c.Template, 50)}」×{c.Count}"));

    private static string FormatSize(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / 1024.0 / 1024:F1}MB" : $"{bytes / 1024.0:F0}KB";

    private static string Shorten(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    private void Log(string line) => AnalysisLog?.Invoke(this, line);
}
