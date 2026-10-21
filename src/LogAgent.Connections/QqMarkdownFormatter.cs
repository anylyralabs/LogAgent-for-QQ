using System.Text;
using System.Text.RegularExpressions;

namespace LogAgent.Connections;

/// <summary>
/// 官方 Bot 的 Markdown 后处理：模型习惯用「【结论】」这类中文方括号分节，
/// 在 QQ Markdown 里既不是标题也不好看。这里把行首标题转成 <c>## 标题</c>，
/// 其余正文里的「【标签】」转成 <c>**标签**</c>，并跳过代码块。
/// </summary>
public static class QqMarkdownFormatter
{
    private static readonly Regex LeadingSection = new(
        @"^[ \t]*【([^】\r\n]{1,24})】[ \t]*[：:]?[ \t]*(.*)$",
        RegexOptions.Compiled);

    private static readonly Regex InlineBracket = new(
        @"【([^】\r\n]{1,24})】",
        RegexOptions.Compiled);

    private static readonly HashSet<string> BareHeadings = new(StringComparer.Ordinal)
    {
        "结论", "最终结论", "原因分析", "可能原因", "根因分析", "根因",
        "解决方法", "解决方案", "修复方法", "修复步骤", "相关建议", "建议",
        "需要补充", "补充信息", "总结", "问题", "现象", "影响",
    };

    public static string Format(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var result = new StringBuilder(normalized.Length + 32);
        var inFence = false;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("```", StringComparison.Ordinal) ||
                trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
                result.Append(line).Append('\n');
                continue;
            }

            if (inFence)
            {
                result.Append(line).Append('\n');
                continue;
            }

            // 行首【标题】→ ## 标题；同一行剩下的正文另起一行，避免标题吃掉第一句。
            var section = LeadingSection.Match(line);
            if (section.Success)
            {
                result.Append("## ").Append(section.Groups[1].Value.Trim()).Append('\n');
                var rest = section.Groups[2].Value.Trim();
                if (rest.Length > 0) result.Append(rest).Append('\n');
                continue;
            }

            // 没有括号的裸标题（模型偶尔会省略【】）。
            var bare = line.Trim().TrimEnd('：', ':').Trim();
            if (line.TrimStart().Length == line.Length && BareHeadings.Contains(bare))
            {
                result.Append("## ").Append(bare).Append('\n');
                continue;
            }

            // 正文里的【标签】→ **标签**
            result.Append(InlineBracket.Replace(line, "**$1**")).Append('\n');
        }

        return result.ToString().TrimEnd('\n');
    }
}
