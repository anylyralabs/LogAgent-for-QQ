using System.Text;

namespace LogAgent.Knowledge;

/// <summary>
/// 知识库文本处理（对齐 LogShare RAG 的中文检索方案）：
/// FTS5 的 unicode61 分词对中文整句无效，因此索引与查询共用同一套切分——
/// CJK 按重叠二元组（bigram）展开、ASCII 按词与字母数字边界拆分并保留原串；
/// 另提供命中上下文截取，长文不整段注入提示词。
/// </summary>
public static class KnowledgeText
{
    private const int MaxQueryTokens = 12;

    private static readonly string[] StopWords =
        ["怎么", "回事", "什么", "这个", "那个", "为什么", "如何", "可以", "还是", "是不是", "帮我", "请问"];

    /// <summary>把文本切成 FTS 词元串（空格分隔，写入 search_title / search_body 列）。</summary>
    public static string ToFtsText(string text) => string.Join(' ', IndexTokens(text));

    /// <summary>查询切分：去停用词、去重、限量（FTS 查询与 LIKE 兜底共用）。</summary>
    public static List<string> QueryTokens(string query) =>
        IndexTokens(query)
            .Where(t => !StopWords.Contains(t))
            .Distinct()
            .Take(MaxQueryTokens)
            .ToList();

    /// <summary>FTS5 MATCH 表达式："tok1" OR "tok2"（词元仅含字母数字与 CJK，无需转义）。</summary>
    public static string? BuildMatchQuery(IReadOnlyList<string> tokens) =>
        tokens.Count == 0 ? null : string.Join(" OR ", tokens.Select(t => $"\"{t}\""));

    /// <summary>
    /// 命中上下文截取：以第一个探测词出现位置为中心开窗（约 budget 字符），截断处回退到行边界。
    /// 对齐 LogShare：短正文完整返回，长正文截取上下文窗口。
    /// </summary>
    public static string ClipAround(string content, IReadOnlyList<string> probes, int budget)
    {
        if (content.Length <= budget) return content;

        var hit = -1;
        foreach (var probe in probes)
        {
            var i = content.IndexOf(probe, StringComparison.OrdinalIgnoreCase);
            if (i >= 0 && (hit < 0 || i < hit)) hit = i;
        }
        if (hit < 0) hit = 0; // 未定位（如纯语义命中）：取头部

        var start = Math.Max(0, hit - budget / 3);
        var end = Math.Min(content.Length, start + budget);

        // 起点尽量对齐到行首（不能越过命中点）
        if (start > 0)
        {
            var nl = content.IndexOf('\n', start);
            if (nl >= 0 && nl <= hit && nl - start < 160) start = nl + 1;
        }

        var slice = content[start..end].Trim();
        return (start > 0 ? "…" : "") + slice + (end < content.Length ? "…" : "");
    }

    /// <summary>
    /// 词元展开：
    /// · ASCII 字母数字段 → 原串（≥2 字符）＋按字母/数字边界拆出的段（≥2）：exit137 → exit、137；
    /// · CJK 连续段 → 相邻二元组：内存不足 → 内存、存不、不足；
    /// · 其余字符（标点/空白等）作为分隔符丢弃。
    /// </summary>
    public static List<string> IndexTokens(string text)
    {
        var tokens = new List<string>();
        var ascii = new StringBuilder();
        var cjk = new StringBuilder();

        void FlushAscii()
        {
            if (ascii.Length > 0) AddAsciiTokens(ascii.ToString(), tokens);
            ascii.Clear();
        }

        void FlushCjk()
        {
            if (cjk.Length > 0) AddCjkTokens(cjk.ToString(), tokens);
            cjk.Clear();
        }

        foreach (var ch in text)
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                FlushCjk();
                ascii.Append(char.ToLowerInvariant(ch));
            }
            else if (IsCjk(ch))
            {
                FlushAscii();
                cjk.Append(ch);
            }
            else
            {
                FlushAscii();
                FlushCjk();
            }
        }
        FlushAscii();
        FlushCjk();
        return tokens;
    }

    private static void AddAsciiTokens(string run, List<string> tokens)
    {
        if (run.Length >= 2) tokens.Add(run);
        var start = 0;
        for (var i = 1; i <= run.Length; i++)
        {
            if (i == run.Length || char.IsDigit(run[i]) != char.IsDigit(run[i - 1]))
            {
                var seg = run[start..i];
                if (seg.Length >= 2 && seg != run) tokens.Add(seg);
                start = i;
            }
        }
    }

    private static void AddCjkTokens(string run, List<string> tokens)
    {
        for (var i = 0; i + 1 < run.Length; i++)
            tokens.Add(run.Substring(i, 2));
    }

    private static bool IsCjk(char c) =>
        (c >= '\u4E00' && c <= '\u9FFF') || (c >= '\u3400' && c <= '\u4DBF') || (c >= '\uF900' && c <= '\uFAFF');
}
