using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using LogAgent.Shared.Configuration;
using LogAgent.Shared.Messaging;

namespace LogAgent.Core;

/// <summary>
/// 消息内容富化（渠道无关）：把「可抓取的链接 / QQ 文件附件」解析成可读文本，并入消息文本供后续管线（会话/RAG/日志分析）使用。
/// · logshare.cn 链接 → LogShare API 抓取日志原文 + 结构化解析（只读；不上传、不调云端 AI）；
/// · QQ 文件附件（.log/.txt 等文本）→ 完整下载落盘到 files 目录（中间内容不丢）；
/// 小文件全文内联；大文件给「本机路径 + 开头/结尾片段 + 工具指引」——模型用 read_file / search_file / run_shell
/// 自己查看任意部分（选择性读取 / 搜索 / shell），而不是由代码截取头尾省略中间。
/// 抓取失败时以【…失败：原因】块并入文本，让模型能如实告知用户。
/// </summary>
public sealed class MessageEnricher
{
    private const int MaxInlineChars = 16000;          // 小于此长度的文本全文内联
    private const int PreviewChars = 700;              // 大文件的头/尾片段长度
    private const long MaxFileBytes = 128 * 1024 * 1024;
    private const long InlineProbeBytes = 256 * 1024;  // ≤ 256KB 的文件全量解码后判断能否内联
    private const int ProbeBytes = 64 * 1024;          // 大文件文本性探测（前 64KB）
    private const int TailSnippetBytes = 4096;

    private readonly string _logShareBase;
    private readonly HttpClient _http;         // 小请求（insights 等，30s）
    private readonly HttpClient _downloadHttp; // 大内容下载（raw 全文 / 文件流式落盘，10 分钟）
    private readonly string _filesDir;

    public MessageEnricher(string logShareBase = "https://api.logshare.cn", HttpClient? http = null, string? filesDir = null)
    {
        _logShareBase = logShareBase.TrimEnd('/');
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _downloadHttp = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _filesDir = filesDir ?? Path.Combine(AppConfigStore.GetConfigDirectory(), "files");
    }

    public event EventHandler<string>? EnrichLog;

    /// <summary>文件暂存目录（QQ 文件与抓取日志完整落盘处）。</summary>
    public string FilesDir => _filesDir;

    private static readonly Regex LogShareLink = new(
        @"https?://(?:www\.)?logshare\.cn/(?:v1/(?:raw|log|insights|ai)/)?([A-Za-z0-9]{5,16})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>富化消息文本：无链接/附件时原样返回。</summary>
    public async Task<string> EnrichTextAsync(BotMessage message, CancellationToken ct = default)
    {
        var blocks = new List<string>();
        foreach (var id in LogShareLink.Matches(message.Text).Select(m => m.Groups[1].Value).Distinct())
            blocks.Add(await FetchLogShareAsync(id, ct));
        if (message.Attachments is { Count: > 0 })
        {
            foreach (var attachment in message.Attachments)
                blocks.Add(await FetchAttachmentAsync(attachment, ct));
        }
        if (blocks.Count == 0) return message.Text;
        var appended = string.Join("\n\n", blocks.Where(b => b.Length > 0));
        return message.Text.Length == 0 ? appended : message.Text + "\n\n" + appended;
    }

    /// <summary>抓取 LogShare 日志（原文 + 结构化解析）；长文完整落盘，模型用工具查看。</summary>
    public async Task<string> FetchLogShareAsync(string id, CancellationToken ct)
    {
        try
        {
            var raw = await _downloadHttp.GetStringAsync($"{_logShareBase}/v1/raw/{id}", ct);
            var insights = "";
            try
            {
                insights = await _http.GetStringAsync($"{_logShareBase}/v1/insights/{id}", ct);
            }
            catch (Exception)
            {
                // 解析失败不影响原文抓取
            }

            if (raw.Length <= MaxInlineChars)
            {
                var sb = new StringBuilder();
                sb.AppendLine($"【已从 LogShare 链接抓取（id={id}，网页 https://logshare.cn/{id}）】");
                if (insights.Length is > 0 and <= 1200) sb.AppendLine($"· 结构化解析：{insights}");
                sb.AppendLine("· 日志正文：");
                sb.Append(raw);
                Log($"[LogShare] 抓取 {id} 成功（{raw.Length} 字符，全文内联）");
                return sb.ToString();
            }

            // 长日志：全文落盘 + 头尾片段 + 工具指引（中间内容不丢）
            var path = StagedPath($"logshare-{id}.log");
            await File.WriteAllTextAsync(path, raw, ct);
            var lineCount = raw.Count(c => c == '\n') + 1;
            var block = new StringBuilder();
            block.AppendLine($"【已从 LogShare 链接抓取全文并保存（id={id}）】（{FormatSize(Encoding.UTF8.GetByteCount(raw))} · 约 {lineCount} 行）");
            block.AppendLine($"网页：https://logshare.cn/{id} · 本机路径：{path}");
            if (insights.Length is > 0 and <= 1200) block.AppendLine($"· 结构化解析：{insights}");
            block.AppendLine("全文都在本地文件里，中间内容不会丢——请用工具按需查看（不要只凭片段下结论）：");
            block.AppendLine(ToolsHint());
            block.AppendLine("--- 开头片段 ---");
            block.AppendLine(HeadSnippet(raw));
            block.AppendLine("--- 结尾片段 ---");
            block.Append(TailSnippet(raw));
            Log($"[LogShare] 抓取 {id} 成功（{raw.Length} 字符，全文已保存）");
            return block.ToString();
        }
        catch (Exception ex)
        {
            Log($"[LogShare] 抓取 {id} 失败：{ex.Message}");
            return $"【LogShare 链接（{id}）抓取失败：{ex.Message}——若用户需要分析，请他直接把日志文本发出来】";
        }
    }

    /// <summary>下载并读取 QQ 文件附件：完整落盘 files 目录；小文件内联，大文件给路径+片段+工具指引。</summary>
    public async Task<string> FetchAttachmentAsync(MessageAttachment attachment, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(attachment.UnavailableReason))
        {
            Log($"[附件] {attachment.Name} 未能读取：{attachment.UnavailableReason}");
            return $"【QQ 文件「{attachment.Name}」（{FormatSize(attachment.Size)}）未能读取：{attachment.UnavailableReason}。可请用户把日志文字直接贴出来，或上传到 logshare.cn 后发链接】";
        }
        string? stagedPath = null;
        try
        {
            if (attachment.Size > MaxFileBytes)
                return $"【QQ 文件「{attachment.Name}」（{FormatSize(attachment.Size)}）超过 {FormatSize(MaxFileBytes)} 上限未读取——建议请用户上传到 logshare.cn 后发链接，或只粘关键段】";

            stagedPath = StagedPath(attachment.Name);
            var (bytes, newlines) = await DownloadToFileAsync(attachment.Url, stagedPath, ct);
            if (bytes == 0)
            {
                TryDelete(stagedPath);
                return $"【QQ 文件「{attachment.Name}」是空文件，无内容】";
            }

            // 文本判定：用文件头探测（BOM/NUL/UTF-8 有效性）
            var probe = ReadHead(stagedPath, ProbeBytes);
            if (!TryDecodeText(attachment.Name, probe, out var probeText))
            {
                TryDelete(stagedPath);
                Log($"[附件] {attachment.Name} 不是可读文本（{FormatSize(bytes)}）");
                return $"【QQ 文件「{attachment.Name}」（{FormatSize(bytes)}）不是可读文本，未读取——可请用户粘贴文本或改发 .log/.txt】";
            }

            // 小文件：全量解码后内联（中间内容不丢）
            if (bytes <= InlineProbeBytes)
            {
                var fullBytes = await File.ReadAllBytesAsync(stagedPath, ct);
                if (TryDecodeText(attachment.Name, fullBytes, out var fullText) && fullText.Length <= MaxInlineChars)
                {
                    Log($"[附件] {attachment.Name} 已读取（{bytes} 字节，全文内联）");
                    return $"【QQ 文件「{attachment.Name}」（{FormatSize(bytes)}）内容】\n{fullText}";
                }
            }

            // 大文件：全文已落盘，给头/尾片段 + 工具指引
            var headSnippet = HeadSnippet(probeText);
            var tailSnippet = TailSnippet(DecodeLoose(ReadTail(stagedPath, TailSnippetBytes)));
            var block = new StringBuilder();
            block.AppendLine($"【QQ 文件「{attachment.Name}」已完整保存到本机】（{FormatSize(bytes)} · 约 {newlines + 1} 行）");
            block.AppendLine($"本机路径：{stagedPath}");
            block.AppendLine("全文都在本地文件里，中间内容不会丢——请用工具按需查看（不要只凭片段下结论）：");
            block.AppendLine(ToolsHint());
            block.AppendLine("--- 开头片段 ---");
            block.AppendLine(headSnippet);
            if (tailSnippet.Length > 0)
            {
                block.AppendLine("--- 结尾片段 ---");
                block.Append(tailSnippet);
            }
            Log($"[附件] {attachment.Name} 已保存（{FormatSize(bytes)}，约 {newlines + 1} 行），等待工具读取");
            return block.ToString();
        }
        catch (Exception ex)
        {
            if (stagedPath is not null) TryDelete(stagedPath);
            Log($"[附件] {attachment.Name} 读取失败：{ex.Message}");
            return $"【QQ 文件「{attachment.Name}」读取失败：{ex.Message}】";
        }
    }

    // ---------- 下载与暂存 ----------

    /// <summary>流式下载到暂存文件（边写边计行数）；超上限中止并抛异常（由调用方清理残件）。用长超时客户端。</summary>
    private async Task<(long Bytes, long Newlines)> DownloadToFileAsync(string url, string path, CancellationToken ct)
    {
        Directory.CreateDirectory(_filesDir);
        using var resp = await _downloadHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(path);
        var buffer = new byte[81920];
        long total = 0, newlines = 0;
        int read;
        while ((read = await src.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            total += read;
            if (total > MaxFileBytes)
                throw new InvalidOperationException($"文件超过 {FormatSize(MaxFileBytes)} 上限，已中止下载");
            newlines += buffer.AsSpan(0, read).Count((byte)'\n');
            await dst.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return (total, newlines);
    }

    /// <summary>生成暂存文件路径（时间戳前缀 + 名字清洗防冲突）。</summary>
    private string StagedPath(string originalName)
    {
        Directory.CreateDirectory(_filesDir);
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        foreach (var ch in originalName) sb.Append(invalid.Contains(ch) ? '_' : ch);
        var safe = sb.ToString().Trim().Trim('.');
        if (safe.Length == 0) safe = "file";
        if (safe.Length > 60) safe = safe[..60];
        var baseName = $"{DateTime.Now:yyyyMMdd-HHmmss}-{safe}";
        var path = Path.Combine(_filesDir, baseName);
        var n = 2;
        while (File.Exists(path)) path = Path.Combine(_filesDir, $"{baseName}_{n++}");
        return path;
    }

    private static string ToolsHint() =>
        "· search_file：搜索关键词定位区段（如 \"Exception\"、\"Caused by\"、\"SIGSEGV\"、\"Mod\"）\n" +
        "· read_file：按行区间精读任意部分（offset / limit）\n" +
        "· run_shell：执行命令进一步处理（工作目录=files 目录，Windows cmd，如 findstr /n /i \"error\" 文件名）";

    private static string HeadSnippet(string text) =>
        text.Length <= PreviewChars ? text : text[..PreviewChars] + "…";

    private static string TailSnippet(string text) =>
        text.Length <= PreviewChars ? text : "…" + text[^PreviewChars..];

    // ---------- 字节与文本辅助 ----------

    private static byte[] ReadHead(string path, int maxBytes)
    {
        using var fs = File.OpenRead(path);
        var take = (int)Math.Min(fs.Length, maxBytes);
        var buf = new byte[take];
        var read = 0;
        while (read < take)
        {
            var n = fs.Read(buf, read, take - read);
            if (n == 0) break;
            read += n;
        }
        return buf[..read];
    }

    private static byte[] ReadTail(string path, int maxBytes)
    {
        using var fs = File.OpenRead(path);
        var take = (int)Math.Min(fs.Length, maxBytes);
        fs.Seek(-take, SeekOrigin.End);
        var buf = new byte[take];
        var read = 0;
        while (read < take)
        {
            var n = fs.Read(buf, read, take - read);
            if (n == 0) break;
            read += n;
        }
        return buf[..read];
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string DecodeLoose(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            bytes = bytes[3..];
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>文本判定与解码：BOM/无 NUL/扩展名与 UTF-8 有效性综合判断。</summary>
    private static bool TryDecodeText(string name, byte[] bytes, out string text)
    {
        text = "";
        if (bytes.Length == 0) return false;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            return true;
        }
        // 采样含 NUL → 二进制
        var probe = Math.Min(bytes.Length, 2048);
        for (var i = 0; i < probe; i++)
            if (bytes[i] == 0)
                return false;

        var ext = Path.GetExtension(name).ToLowerInvariant();
        var knownText = ext is ".log" or ".txt" or ".json" or ".yml" or ".yaml" or ".properties" or ".md" or ".xml" or ".crash";
        if (!knownText && ext.Length > 0) return false; // 已知非文本扩展名（.zip/.png…）直接拒绝

        var decoded = Encoding.UTF8.GetString(bytes);
        // 大量替换符（U+FFFD）说明不是 UTF-8 文本
        var bad = 0;
        foreach (var ch in decoded)
            if (ch == '\uFFFD' && ++bad > bytes.Length / 50)
                return false;
        text = decoded;
        return true;
    }

    private static string FormatSize(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / 1024.0 / 1024:F1}MB" : $"{bytes / 1024.0:F0}KB";

    private void Log(string line) => EnrichLog?.Invoke(this, line);
}
