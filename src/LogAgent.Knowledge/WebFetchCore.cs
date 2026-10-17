using System.Net;
using System.Net.Sockets;
using System.Text;
using HtmlAgilityPack;

namespace LogAgent.Knowledge;

/// <summary>网页抓取结果：标题 + 正文文本。</summary>
public sealed record WebPage(string Url, string Title, string Text, long Bytes, bool Truncated);

/// <summary>
/// 网页抓取核心（富化与 web_fetch 工具共用）：
/// SSRF 守卫（仅 http/https，拒绝环回/内网/链路本地地址，重定向逐跳复查）+
/// 大小上限 + HTML 提取（标题/正文，去 script/style/nav）。
/// </summary>
public static class WebFetchCore
{
    private const long MaxPageBytes = 4 * 1024 * 1024;

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        c.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36 LogAgent/0.7");
        c.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,text/plain;q=0.9,*/*;q=0.5");
        return c;
    }

    /// <summary>SSRF 守卫：仅 http/https，且主机不得解析到环回/私网/链路本地地址。</summary>
    public static async Task EnsurePublicHttpUrlAsync(string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("仅支持 http/https 网页地址");
        if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("拒绝访问本机/内网地址");

        IPAddress[] addresses;
        try
        {
            addresses = uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6
                ? [IPAddress.Parse(uri.Host)]
                : await Dns.GetHostAddressesAsync(uri.Host, ct);
        }
        catch (SocketException)
        {
            throw new InvalidOperationException("域名解析失败");
        }

        if (IsBlockedAddresses(addresses))
            throw new InvalidOperationException("拒绝访问本机/内网地址");
    }

    /// <summary>地址黑名单判定（纯函数，便于单测）：环回 / RFC1918 / CGNAT / 链路本地 / IPv6 ULA。</summary>
    public static bool IsBlockedAddresses(IReadOnlyCollection<IPAddress> addresses)
    {
        foreach (var ip in addresses)
        {
            if (IPAddress.IsLoopback(ip)) return true;
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = ip.GetAddressBytes();
                if (b[0] == 10 || b[0] == 127) return true;
                if (b[0] == 172 && (b[1] & 0xF0) == 16) return true;
                if (b[0] == 192 && b[1] == 168) return true;
                if (b[0] == 169 && b[1] == 254) return true;
                if (b[0] == 100 && (b[1] & 0xC0) == 64) return true; // CGNAT 100.64/10
            }
            else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return true;
                if (ip.GetAddressBytes()[0] == 0xFD) return true; // ULA fc00::/7
            }
        }
        return false;
    }

    /// <summary>
    /// 抓取网页并提取标题与正文文本。
    /// 默认执行 SSRF 守卫（内网测试环境传 allowPrivate: true 跳过）；重定向最多跟 3 跳且逐跳复查。
    /// </summary>
    public static async Task<WebPage> FetchAsync(string url, bool allowPrivate = false, int maxTextChars = 8000, CancellationToken ct = default)
    {
        if (!allowPrivate) await EnsurePublicHttpUrlAsync(url, ct);
        var current = url;
        for (var hop = 0; hop <= 3; hop++)
        {
            using var resp = await Http.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, ct);

            if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location is { } loc)
            {
                current = new Uri(new Uri(current), loc).ToString();
                if (!allowPrivate) await EnsurePublicHttpUrlAsync(current, ct);
                continue;
            }

            resp.EnsureSuccessStatusCode();
            var media = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (media is not ("text/html" or "application/xhtml+xml" or "text/plain"))
                throw new InvalidOperationException($"非文本网页（{media}），无法阅读");

            using var ms = new MemoryStream();
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var buf = new byte[64 * 1024];
            int n;
            while ((n = await stream.ReadAsync(buf, ct)) > 0)
            {
                ms.Write(buf, 0, n);
                if (ms.Length > MaxPageBytes) throw new InvalidOperationException("网页过大（>4MB），不抓取");
            }

            var body = Decode(ms.ToArray(), media);
            return Extract(current, body, media, ms.Length, maxTextChars);
        }
        throw new InvalidOperationException("重定向次数过多");
    }

    private static string Decode(byte[] bytes, string media)
    {
        var utf8 = Encoding.UTF8.GetString(bytes);
        // 有显式非 UTF-8 charset 且 UTF-8 解码出现大量替换符时，按 Latin-1 兜底（保底可读）
        var header = utf8[..Math.Min(2048, utf8.Length)].ToLowerInvariant();
        var explicitCharset = header.Contains("charset=gb") || header.Contains("charset=\"gb");
        var bad = utf8.Count(c => c == '\uFFFD');
        if (bad > utf8.Length / 50 && !explicitCharset)
            return Encoding.GetEncoding("ISO-8859-1").GetString(bytes);
        return utf8;
    }

    /// <summary>HTML → (标题, 正文)。text/plain 原样返回。</summary>
    public static WebPage Extract(string url, string body, string mediaType, long bytes, int maxChars)
    {
        if (mediaType == "text/plain")
        {
            var plain = body.Trim();
            var truncatedPlain = plain.Length > maxChars;
            return new WebPage(url, url, truncatedPlain ? plain[..maxChars] : plain, bytes, truncatedPlain);
        }

        var doc = new HtmlDocument();
        doc.LoadHtml(body);
        var title = System.Net.WebUtility.HtmlDecode(
            doc.DocumentNode.SelectSingleNode("//title")?.InnerText?.Trim() ?? "");

        var root = doc.DocumentNode.SelectSingleNode("//body") ?? doc.DocumentNode;
        foreach (var xpath in new[] { "//script", "//style", "//nav", "//header", "//footer", "//noscript", "//aside" })
            foreach (var node in root.SelectNodes(xpath)?.ToArray() ?? [])
                node.Remove();

        var text = System.Net.WebUtility.HtmlDecode(root.InnerText);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        var truncated = text.Length > maxChars;
        return new WebPage(url, string.IsNullOrWhiteSpace(title) ? url : title,
            truncated ? text[..maxChars] : text, bytes, truncated);
    }
}
