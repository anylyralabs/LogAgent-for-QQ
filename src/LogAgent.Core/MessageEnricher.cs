using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using LogAgent.Shared.Configuration;
using LogAgent.Shared.Messaging;
using LogAgent.Shared.Platform;

namespace LogAgent.Core;

/// <summary>
/// 消息内容富化（渠道无关）：把「可抓取的链接 / QQ 文件附件」解析成可读文本，并入消息文本供后续管线（会话/RAG/日志分析）使用。
/// · logshare.cn 链接 → LogShare API 抓取日志原文 + 结构化解析（只读；不上传、不调云端 AI）；
/// · QQ 文件附件（.log/.txt 等文本）→ 完整下载落盘到 files 目录（中间内容不丢）；
/// · .zip/.tar.gz 等压缩包 → 只保存原文件并给出本机路径 + shell 解压指引，由模型自己 run_shell 解压、
///   再用 search_file / read_file 读取，不在 C# 里硬实现解压；
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
    private readonly string _mcLogsBase;
    private readonly bool _allowPrivateWebFetch;

    public MessageEnricher(string logShareBase = "https://api.logshare.cn", HttpClient? http = null, string? filesDir = null,
        string? mcLogsBase = null, bool allowPrivateWebFetch = false)
    {
        _logShareBase = logShareBase.TrimEnd('/');
        _mcLogsBase = (mcLogsBase ?? "https://api.mclo.gs").TrimEnd('/');
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _downloadHttp = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _filesDir = filesDir ?? AppConfigStore.GetFilesDirectory();
        _allowPrivateWebFetch = allowPrivateWebFetch;
    }

    public event EventHandler<string>? EnrichLog;

    /// <summary>文件暂存目录（QQ 文件与抓取日志完整落盘处）。</summary>
    public string FilesDir => _filesDir;

    /// <summary>
    /// 共享日志平台链接（raw + insights 端点同构，可互换）：
    /// logshare.cn（/v1）、mclo.gs 与 api.mclo.gs（/1）。
    /// </summary>
    private static readonly Regex SharedLogLink = new(
        @"https?://(?:www\.)?(?:logshare\.cn(?:/v1/(?:raw|log|insights|ai))?|mclo\.gs(?:/1/(?:raw|insights))?|api\.mclo\.gs/1/(?:raw|insights))/(?<id>[A-Za-z0-9]{4,16})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>富化消息文本：无链接/附件时原样返回。</summary>
    public async Task<string> EnrichTextAsync(BotMessage message, CancellationToken ct = default)
    {
        var blocks = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in SharedLogLink.Matches(message.Text).Cast<Match>())
        {
            var key = m.Value.Contains("logshare", StringComparison.OrdinalIgnoreCase) ? "logshare" : "mclogs";
            if (!seen.Add($"{key}:{m.Groups["id"].Value}")) continue;
            blocks.Add(await FetchSharedLogAsync(key, m.Groups["id"].Value, ct));
        }
        // 一般网页链接（排除已处理的共享日志平台）：自动抓取正文阅读，每条消息最多 2 条
        foreach (var url in GeneralWebUrl.Matches(message.Text)
                     .Select(m => m.Value.TrimEnd('.', '，', '。', '）', ')', '"', '\''))
                     .Where(u => !u.Contains("logshare.cn", StringComparison.OrdinalIgnoreCase)
                              && !u.Contains("mclo.gs", StringComparison.OrdinalIgnoreCase))
                     .Where(u => seen.Add(u))
                     .Take(2))
            blocks.Add(await FetchWebPageAsync(url, ct));
        if (message.Attachments is { Count: > 0 })
        {
            foreach (var attachment in message.Attachments)
                blocks.Add(await FetchAttachmentAsync(attachment, ct));
        }
        if (blocks.Count == 0) return message.Text;
        var appended = string.Join("\n\n", blocks.Where(b => b.Length > 0));
        return message.Text.Length == 0 ? appended : message.Text + "\n\n" + appended;
    }

    private static readonly Regex GeneralWebUrl = new(
        @"https?://[^\s<>()\[\]""'，]+", RegexOptions.Compiled);

    /// <summary>抓取一般网页：提取标题与正文，注入阅读块（SSRF 守卫默认开启）。</summary>
    private async Task<string> FetchWebPageAsync(string url, CancellationToken ct)
    {
        try
        {
            var page = await Knowledge.WebFetchCore.FetchAsync(url, allowPrivate: _allowPrivateWebFetch, maxTextChars: 8000, ct);
            Log($"[网页] 抓取 {url} 成功（{page.Text.Length} 字符）");
            var sb = new StringBuilder();
            sb.AppendLine($"【已抓取网页：{page.Title}】({url})");
            if (page.Truncated) sb.AppendLine("（正文较长，仅前 8000 字符——需要更多内容可让模型用 web_fetch 工具抓取）");
            sb.Append(page.Text);
            return sb.ToString();
        }
        catch (Exception ex)
        {
            Log($"[网页] 抓取 {url} 失败：{ex.Message}");
            return $"【网页抓取失败：{url}——{ex.Message}】（可用 web_fetch 工具重试，或请用户直接粘贴正文）";
        }
    }

    /// <summary>兼容入口：抓取 LogShare 日志。</summary>
    public Task<string> FetchLogShareAsync(string id, CancellationToken ct) => FetchSharedLogAsync("logshare", id, ct);

    /// <summary>抓取共享日志平台日志（原文 + 结构化解析）；长文完整落盘，模型用工具查看。</summary>
    public async Task<string> FetchSharedLogAsync(string providerKey, string id, CancellationToken ct)
    {
        var isLogShare = providerKey == "logshare";
        var label = isLogShare ? "LogShare" : "mclo.gs";
        var tag = isLogShare ? "LogShare" : "McLogs";
        var baseUrl = isLogShare ? _logShareBase : _mcLogsBase;
        var seg = isLogShare ? "/v1" : "/1";
        var web = isLogShare ? $"https://logshare.cn/{id}" : $"https://mclo.gs/{id}";
        try
        {
            var raw = await _downloadHttp.GetStringAsync($"{baseUrl}{seg}/raw/{id}", ct);
            var insights = "";
            try
            {
                insights = await _http.GetStringAsync($"{baseUrl}{seg}/insights/{id}", ct);
            }
            catch (Exception)
            {
                // 解析失败不影响原文抓取
            }

            if (raw.Length <= MaxInlineChars)
            {
                var sb = new StringBuilder();
                sb.AppendLine($"【已从 {label} 链接抓取（id={id}，网页 {web}）】");
                if (insights.Length is > 0 and <= 1200) sb.AppendLine($"· 结构化解析：{insights}");
                sb.AppendLine("· 日志正文：");
                sb.Append(raw);
                Log($"[{tag}] 抓取 {id} 成功（{raw.Length} 字符，全文内联）");
                return sb.ToString();
            }

            // 长日志：全文落盘 + 头尾片段 + 工具指引（中间内容不丢）
            var path = StagedPath($"{providerKey}-{id}.log");
            await File.WriteAllTextAsync(path, raw, ct);
            var lineCount = raw.Count(c => c == '\n') + 1;
            var block = new StringBuilder();
            block.AppendLine($"【已从 {label} 链接抓取全文并保存（id={id}）】（{FormatSize(Encoding.UTF8.GetByteCount(raw))} · 约 {lineCount} 行）");
            block.AppendLine($"网页：{web} · 本机路径：{path}");
            if (insights.Length is > 0 and <= 1200) block.AppendLine($"· 结构化解析：{insights}");
            block.AppendLine("全文都在本地文件里，中间内容不会丢——请用工具按需查看（不要只凭片段下结论）：");
            block.AppendLine(ToolsHint());
            block.AppendLine("--- 开头片段 ---");
            block.AppendLine(HeadSnippet(raw));
            block.AppendLine("--- 结尾片段 ---");
            block.Append(TailSnippet(raw));
            Log($"[{tag}] 抓取 {id} 成功（{raw.Length} 字符，全文已保存）");
            return block.ToString();
        }
        catch (Exception ex)
        {
            Log($"[{tag}] 抓取 {id} 失败：{ex.Message}");
            return $"【{label} 链接（id={id}）抓取失败：{ex.Message}】\n可请用户直接把日志文本粘贴出来，或改用 logshare.cn 分享后再发链接。";
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

            // 压缩包不在 C# 里硬解压：保留原文件，让模型用 run_shell 自己列清单/解压，
            // 再用 search_file / read_file 读取解压出的日志。
            if (IsArchive(attachment.Name))
            {
                Log($"[附件] {attachment.Name} 是压缩包，已保存（{FormatSize(bytes)}），等待 shell 工具解压/读取");
                return ArchiveAttachmentBlock(attachment, stagedPath, bytes);
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

    // ---------- 压缩包：只落盘 + 给 shell 指引，不在代码里解压 ----------

    private static readonly string[] ArchiveSuffixes = [".zip", ".tar.gz", ".tgz", ".tar", ".7z", ".rar"];

    private static bool IsArchive(string name) =>
        ArchiveSuffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase));

    private static string ArchiveAttachmentBlock(MessageAttachment attachment, string path, long bytes)
    {
        var lower = attachment.Name.ToLowerInvariant();
        var commands = lower.EndsWith(".zip", StringComparison.Ordinal)
            ? $"""
              · python3 -c "import zipfile; zipfile.ZipFile(r'{path}').extractall(r'{path}.unzipped')"  # 默认可用的解压方式
              · unzip -l "{path}" 或 unzip -o "{path}" -d "{path}.unzipped"  # 若沙箱白名单允许 unzip
              · Windows 可用：python -c "import zipfile; zipfile.ZipFile(r'{path}').extractall(r'{path}.unzipped')"
              """
            : lower.EndsWith(".tar.gz", StringComparison.Ordinal) || lower.EndsWith(".tgz", StringComparison.Ordinal)
                ? $"""
                  · python3 -c "import tarfile; tarfile.open(r'{path}','r:gz').extractall(r'{path}.unzipped')"
                  · tar -tzf "{path}" 或 tar -xzf "{path}" -C "{path}.unzipped"  # 若沙箱白名单允许 tar
                  """
                : lower.EndsWith(".tar", StringComparison.Ordinal)
                    ? $"""
                      · python3 -c "import tarfile; tarfile.open(r'{path}','r').extractall(r'{path}.unzipped')"
                      · tar -tf "{path}" 或 tar -xf "{path}" -C "{path}.unzipped"  # 若沙箱白名单允许 tar
                      """
                    : $"""
                      · 7z l "{path}" 或 unrar l "{path}"                   # 查看清单
                      · 7z x "{path}" -o"{path}.unzipped" 或 unrar x "{path}" "{path}.unzipped"
                      """;

        return $"""
            【QQ 文件「{attachment.Name}」已完整保存到本机】（{FormatSize(bytes)}）
            本机路径：{path}
            这是压缩包，不要直接回复“读不了”。请用 run_shell 先列清单/解压，再用 search_file / read_file 读取解压出的 .log/.txt：
            {commands}
            解压后优先搜索 Exception / ERROR / FATAL / SIGSEGV / crash / 退出码 / Mod 等标记。
            """;
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
        "· " + PlatformShell.Current.RunShellOneLiner;

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
