using System.Text;
using System.Text.RegularExpressions;

namespace LogAgent.Knowledge;

/// <summary>
/// 文档导入管道（对齐 LogShare 知识库规范）：
/// · 以「## 」二级标题为切块单元，标题参与检索加权（FTS search_title 10:1）；
/// · 超长节先按「### 」子标题拆，仍超长按段落打包，单块约 ≤1500 字符；
/// · 文件标题取「# H1」或顶部「====」块（日志条目格式），否则用文件名；
/// · 同一来源文件重复导入 = 替换旧块（幂等），source=doc 直接生效。
/// </summary>
public sealed class KnowledgeImporter(KnowledgeStore store)
{
    public const string Source = "doc";

    private const int MaxSectionChars = 1500;
    private const int PackTarget = 1300;
    private const int MinChunkChars = 20;

    private static readonly string[] AllowedExtensions = [".md", ".txt", ".log"];

    public sealed record ImportResult(int Files, int Chunks, int Problems, IReadOnlyList<string> ProblemFiles);

    public event EventHandler<string>? ImportLog;

    /// <summary>导入文件或目录（目录递归；返回汇总统计）。</summary>
    public async Task<ImportResult> ImportPathAsync(string path, CancellationToken ct = default)
    {
        var root = Path.GetFullPath(path);
        List<string> files;
        string baseDir;
        if (File.Exists(root))
        {
            files = [root];
            baseDir = Path.GetDirectoryName(root) ?? root;
        }
        else if (Directory.Exists(root))
        {
            files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(IsSupported)
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();
            baseDir = root;
        }
        else
        {
            throw new FileNotFoundException($"路径不存在：{path}");
        }

        var importedFiles = 0;
        var totalChunks = 0;
        var problems = new List<string>();
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var chunks = await ImportFileAsync(file, baseDir, ct);
                if (chunks > 0)
                {
                    importedFiles++;
                    totalChunks += chunks;
                }
            }
            catch (Exception ex)
            {
                problems.Add(file);
                Log($"导入失败 {Path.GetFileName(file)}：{ex.Message}");
            }
        }
        Log($"导入完成：{importedFiles} 个文件 / {totalChunks} 条知识（扫描 {files.Count} 个文件，失败 {problems.Count}）");
        return new ImportResult(importedFiles, totalChunks, problems.Count, problems);
    }

    /// <summary>导入单个文件：切块 → 替换旧块 → 入库。返回切块数。</summary>
    public async Task<int> ImportFileAsync(string filePath, string baseDir, CancellationToken ct = default)
    {
        var text = await File.ReadAllTextAsync(filePath, ct);
        var chunks = SplitChunks(text, Path.GetFileNameWithoutExtension(filePath))
            .Where(c => c.Body.Trim().Length >= MinChunkChars)
            .ToList();
        if (chunks.Count == 0) return 0;

        var rel = Path.GetRelativePath(baseDir, filePath).Replace('\\', '/');
        store.DeleteBySourceRef(Source, rel); // 幂等：重复导入替换
        await store.AddRangeAsync(chunks.Select(c => ((string?)c.Title, c.Body)).ToList(), Source, rel, ct);
        Log($"[{rel}] 切块 {chunks.Count} 条");
        return chunks.Count;
    }

    private static bool IsSupported(string file) =>
        AllowedExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 切块：返回 (标题, 正文) 列表。
    /// </summary>
    public static List<(string Title, string Body)> SplitChunks(string text, string fallbackTitle)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var (docTitle, rest) = ExtractTitle(normalized, fallbackTitle);
        rest = rest.Trim();
        if (rest.Length == 0) return [];

        // 按「## 」收集二级节（前言归入 docTitle 名下）
        var sections = new List<(string Title, string Body, bool IsPreamble)>();
        var currentTitle = docTitle;
        var currentLines = new List<string>();
        var isPreamble = true;
        foreach (var line in rest.Split('\n'))
        {
            if (line.StartsWith("## ") && !line.StartsWith("### "))
            {
                var body = string.Join('\n', currentLines).Trim();
                if (body.Length > 0) sections.Add((currentTitle, body, isPreamble));
                currentTitle = CleanHeading(line[3..]);
                currentLines.Clear();
                isPreamble = false;
            }
            else
            {
                currentLines.Add(line);
            }
        }
        var lastBody = string.Join('\n', currentLines).Trim();
        if (lastBody.Length > 0) sections.Add((currentTitle, lastBody, isPreamble));

        var chunks = new List<(string, string)>();
        foreach (var (title, body, preamble) in sections)
        {
            if (preamble)
            {
                // 无 ## 的整文：整文成块；有 ## 时前言太短（如"文档更新日期"）丢弃
                if (sections.Count == 1 || body.Length >= 50)
                    chunks.AddRange(PackParagraphs(title, body));
            }
            else
            {
                chunks.AddRange(SplitSection(title, body));
            }
        }
        return chunks;
    }

    /// <summary>超长二级节：先按 ### 子标题拆，仍超长按段落打包。</summary>
    private static List<(string, string)> SplitSection(string title, string body)
    {
        if (title.Length + body.Length <= MaxSectionChars) return [(title, body)];

        var subs = new List<(string Title, string Body, bool IsPreamble)>();
        string? subTitle = null;
        var buf = new List<string>();
        foreach (var line in body.Split('\n'))
        {
            if (line.StartsWith("### ") && !line.StartsWith("#### "))
            {
                var subBody = string.Join('\n', buf).Trim();
                if (subBody.Length > 0) subs.Add((subTitle ?? title, subBody, subTitle is null));
                subTitle = CleanHeading(line[4..]);
                buf.Clear();
            }
            else
            {
                buf.Add(line);
            }
        }
        var tail = string.Join('\n', buf).Trim();
        if (tail.Length > 0) subs.Add((subTitle ?? title, tail, subTitle is null));

        if (subs.Count < 2) return PackParagraphs(title, body);

        var result = new List<(string, string)>();
        foreach (var (t, b, pre) in subs)
            result.AddRange(PackParagraphs(pre ? title : t, b));
        return result;
    }

    /// <summary>按空行段落打包，单块约 ≤MaxSectionChars；超长段落硬拆。</summary>
    private static List<(string, string)> PackParagraphs(string title, string body)
    {
        if (body.Length <= MaxSectionChars) return [(title, body)];

        var result = new List<(string, string)>();
        var buf = new StringBuilder();
        void Flush()
        {
            var text = buf.ToString().Trim();
            if (text.Length > 0) result.Add((title, text));
            buf.Clear();
        }

        foreach (var raw in body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var para = raw.Trim();
            if (para.Length == 0) continue;
            if (para.Length > MaxSectionChars)
            {
                Flush();
                foreach (var piece in HardSplit(para, PackTarget))
                    if (piece.Length >= MinChunkChars) result.Add((title, piece));
                continue;
            }
            if (buf.Length > 0 && buf.Length + para.Length + 2 > PackTarget) Flush();
            if (buf.Length > 0) buf.Append("\n\n");
            buf.Append(para);
        }
        Flush();
        return result;
    }

    /// <summary>硬拆超长段落，尽量在换行处断开。</summary>
    private static IEnumerable<string> HardSplit(string text, int size)
    {
        for (var i = 0; i < text.Length; i += size)
        {
            var len = Math.Min(size, text.Length - i);
            if (i + len < text.Length)
            {
                var nl = text.LastIndexOf('\n', i + len - 1, len);
                if (nl > i + size / 2) len = nl - i;
            }
            yield return text.Substring(i, len).Trim();
        }
    }

    /// <summary>提取文件标题：优先「# H1」，其次顶部「====」围栏块（日志条目格式），否则用文件名。</summary>
    private static (string Title, string Remaining) ExtractTitle(string text, string fallbackTitle)
    {
        var lines = text.Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
            {
                if (i >= 3) break; // 只允许标题前少量空行
                continue;
            }
            if (line.StartsWith("# ") && !line.StartsWith("## "))
            {
                var title = CleanHeading(line[2..]);
                var rest = string.Join('\n', lines.Take(i).Concat(lines.Skip(i + 1)));
                return (title.Length > 0 ? title : fallbackTitle, rest);
            }
            break;
        }

        // 顶部 ==== 围栏块（1~3 行标题）
        var start = 0;
        while (start < lines.Length && lines[start].Trim().Length == 0) start++;
        if (start < lines.Length && IsFence(lines[start]))
        {
            var inner = new List<string>();
            var j = start + 1;
            while (j < lines.Length && j - start <= 6 && !IsFence(lines[j]))
            {
                if (lines[j].Trim().Length > 0) inner.Add(lines[j].Trim());
                j++;
            }
            if (j < lines.Length && IsFence(lines[j]) && inner.Count is > 0 and <= 3)
            {
                var rest = string.Join('\n', lines.Take(start).Concat(lines.Skip(j + 1)));
                return (string.Join(' ', inner), rest);
            }
        }
        return (fallbackTitle, text);
    }

    private static bool IsFence(string line)
    {
        var t = line.Trim();
        return t.Length >= 4 && t.All(c => c == '=');
    }

    /// <summary>清理标题：去序号前缀（一、/ 1、/ 1. （带空格））与装饰字符。</summary>
    private static string CleanHeading(string raw)
    {
        var t = raw.Trim().TrimEnd('：', ':', '#', ' ').Trim();
        t = Regex.Replace(t, @"^(?:[一二三四五六七八九十]+、|\d{1,2}、|\d{1,2}[.．]\s+)", "");
        return t.Trim();
    }

    private void Log(string line) => ImportLog?.Invoke(this, line);
}
