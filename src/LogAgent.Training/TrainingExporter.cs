using System.Text;
using System.Text.Json;
using LogAgent.Memory;

namespace LogAgent.Training;

public enum ExportFormat { OpenAiChat, ShareGpt, Alpaca, DpoPair }

/// <summary>训练数据导出管道（规划 3.8）：质量标签筛选 → 强制脱敏 → 四种 JSONL 格式 → 审计记录。</summary>
public sealed class TrainingExporter(SessionStore store)
{
    public sealed record ExportOptions(
        ExportFormat Format,
        IReadOnlyCollection<string> QualityTags,
        bool Redact = true,
        int Limit = 50000);

    public sealed record ExportResult(string Path, int Lines, int Skipped, string AuditEntry);

    /// <summary>导出训练数据 JSONL。返回（输出路径，写入行数，跳过条数，审计描述）。</summary>
    public Task<ExportResult> ExportAsync(string outputPath, ExportOptions options, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        var allTurns = store.ListRecentTurns(options.Limit)
            .Where(t => t.AgentReply is { Length: > 0 } &&
                        options.QualityTags.Contains(t.QualityTag))
            .OrderBy(t => t.Id)
            .ToList();

        var lines = new List<string>();
        var skipped = 0;

        if (options.Format == ExportFormat.DpoPair)
        {
            // DPO：同一 chat 中相同提问的多轮（用户纠正重问场景）→ 早回复=rejected，晚回复=chosen
            var groups = allTurns
                .GroupBy(t => (t.ChatId, t.UserInput))
                .Where(g => g.Count() >= 2);
            foreach (var g in groups)
            {
                var ordered = g.OrderBy(t => t.Id).ToList();
                var rejected = ordered[0];
                var chosen = ordered[^1];
                var prompt = MaybeRedact(chosen.UserInput, options);
                lines.Add(JsonSerializer.Serialize(new
                {
                    prompt,
                    chosen = MaybeRedact(chosen.AgentReply, options),
                    rejected = MaybeRedact(rejected.AgentReply, options),
                    source = "group_chat",
                    session = chosen.SessionId,
                }));
            }
            skipped = allTurns.Count - lines.Count;
        }
        else
        {
            foreach (var turn in allTurns)
            {
                if (turn.UserInput.Trim().Length < 2 || turn.AgentReply.Trim().Length < 2)
                {
                    skipped++;
                    continue;
                }
                var input = MaybeRedact(turn.UserInput, options);
                var output = MaybeRedact(turn.AgentReply, options);
                var line = options.Format switch
                {
                    ExportFormat.OpenAiChat => JsonSerializer.Serialize(new
                    {
                        messages = new object[]
                        {
                            new { role = "user", content = input },
                            new { role = "assistant", content = output },
                        },
                        source = "group_chat",
                        quality = turn.QualityTag,
                        model = turn.ModelUsed,
                    }),
                    ExportFormat.ShareGpt => JsonSerializer.Serialize(new
                    {
                        conversations = new object[]
                        {
                            new { from = "human", value = input },
                            new { from = "gpt", value = output },
                        },
                        source = "group_chat",
                        quality = turn.QualityTag,
                    }),
                    ExportFormat.Alpaca => JsonSerializer.Serialize(new
                    {
                        instruction = input,
                        input = "",
                        output,
                        source = "group_chat",
                        quality = turn.QualityTag,
                    }),
                    _ => throw new ArgumentOutOfRangeException(nameof(options)),
                };
                lines.Add(line);
            }
        }

        File.WriteAllLines(outputPath, lines, new UTF8Encoding(false));

        // 导出审计（同目录 append）
        var auditPath = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(outputPath))!,
            "export_audit.jsonl");
        var audit = JsonSerializer.Serialize(new
        {
            ts = DateTimeOffset.UtcNow.ToString("O"),
            path = outputPath,
            format = options.Format.ToString(),
            redacted = options.Redact,
            lines = lines.Count,
            skipped,
            tags = options.QualityTags,
        });
        File.AppendAllText(auditPath, audit + "\n", new UTF8Encoding(false));

        return Task.FromResult(new ExportResult(outputPath, lines.Count, skipped, audit));
    }

    private static string MaybeRedact(string text, ExportOptions options) =>
        options.Redact ? Redactor.Apply(text) : text;
}
