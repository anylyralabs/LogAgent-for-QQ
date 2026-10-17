using System.Text.RegularExpressions;

namespace LogAgent.Core;

/// <summary>日志解析：逐行提取级别与消息（多格式通用启发式）。</summary>
public static partial class LogParser
{
    public sealed record LogEntry(int LineNo, string Level, string Message, string Raw);

    [GeneratedRegex(@"\b(FATAL|CRITICAL|ERROR|ERR|WARN(?:ING)?|INFO|DEBUG|TRACE|NOTICE)\b", RegexOptions.IgnoreCase)]
    private static partial Regex LevelRegex();

    public static List<LogEntry> Parse(string logText)
    {
        var entries = new List<LogEntry>();
        var lineNo = 0;
        foreach (var line in logText.Split('\n'))
        {
            lineNo++;
            var trimmed = line.TrimEnd('\r');
            var match = LevelRegex().Match(trimmed);
            var level = match.Success ? NormalizeLevel(match.Groups[1].Value) : "";
            entries.Add(new LogEntry(lineNo, level, trimmed, trimmed));
        }
        return entries;
    }

    private static string NormalizeLevel(string raw) => raw.ToLowerInvariant() switch
    {
        "fatal" or "critical" => "FATAL",
        "error" or "err" => "ERROR",
        "warn" or "warning" => "WARN",
        "info" or "notice" => "INFO",
        _ => raw.ToUpperInvariant(),
    };
}

/// <summary>本地日志统计（不烧 token）：级别分布、错误模板聚类（Drain 思想简化版）、堆栈提取。</summary>
public static class LogStats
{
    public sealed record ErrorCluster(string Template, int Count, List<string> Samples);

    public sealed record Stats(
        int TotalLines,
        int EmptyLines,
        Dictionary<string, int> LevelCounts,
        List<ErrorCluster> TopErrors,
        List<string> StackTraces,
        DateTimeOffset ComputedAt)
    {
        public int ErrorLike => LevelCounts.GetValueOrDefault("ERROR") + LevelCounts.GetValueOrDefault("FATAL");
    }

    public static Stats Compute(List<LogParser.LogEntry> entries)
    {
        var levels = new Dictionary<string, int>();
        var templates = new Dictionary<string, (int Count, LogParser.LogEntry First)>();

        var stackTraces = new List<string>();
        var currentStack = new List<string>();

        foreach (var e in entries)
        {
            if (e.Level.Length > 0)
                levels[e.Level] = levels.GetValueOrDefault(e.Level) + 1;

            if ((e.Level is "ERROR" or "FATAL" || LooksLikeException(e.Message)) && !IsStackLine(e.Message))
            {
                var template = Template(e.Message);
                var (count, first) = templates.GetValueOrDefault(template);
                templates[template] = (count + 1, first ?? e);
            }

            if (IsStackLine(e.Message))
            {
                currentStack.Add(e.Message);
            }
            else if (currentStack.Count > 0)
            {
                if (currentStack.Count >= 3) stackTraces.Add(string.Join("\n", currentStack.Take(20)));
                currentStack.Clear();
            }
        }
        if (currentStack.Count >= 3) stackTraces.Add(string.Join("\n", currentStack.Take(20)));

        var topErrors = templates
            .OrderByDescending(kv => kv.Value.Count)
            .Take(8)
            .Select(kv => new ErrorCluster(kv.Key, kv.Value.Count,
                entries.Where(e => Template(e.Message) == kv.Key).Take(2).Select(e => e.Message).ToList()))
            .ToList();

        return new Stats(
            entries.Count,
            entries.Count(e => e.Message.Length == 0),
            levels,
            topErrors,
            stackTraces.Take(3).ToList(),
            DateTimeOffset.Now);
    }

    /// <summary>消息模板化：数字/UUID/IP/HEX/引号串 → 占位符，同类错误聚成一簇。</summary>
    public static string Template(string message)
    {
        var t = message;
        t = Regex.Replace(t, @"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", "<UUID>");
        t = Regex.Replace(t, @"\b(?:\d{1,3}\.){3}\d{1,3}(?::\d+)?\b", "<IP>");
        t = Regex.Replace(t, @"\b\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}[^\s]*\b", "<TS>");
        t = Regex.Replace(t, @"\b[0-9a-fA-F]{16,}\b", "<HEX>");
        t = Regex.Replace(t, @"\d+", "<N>");
        t = Regex.Replace(t, @"""[^""]*""", "<S>");
        return t.Length > 160 ? t[..160] : t;
    }

    private static bool LooksLikeException(string message) =>
        message.Contains("Exception") || message.Contains(" at ") && message.Contains("(");

    private static bool IsStackLine(string line) =>
        line.StartsWith("   at ", StringComparison.Ordinal) ||
        line.StartsWith("\tat ", StringComparison.Ordinal) ||
        line.StartsWith("Caused by:", StringComparison.OrdinalIgnoreCase);
}
