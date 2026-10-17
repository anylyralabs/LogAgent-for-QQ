using HtmlAgilityPack;

namespace LogAgent.Knowledge;

public interface IWebFetcher
{
    Task<string> FetchTextAsync(string url, CancellationToken ct = default);
}

/// <summary>网页抓取 + 正文提取（学习 Agent 的 WebTools 底座，规划 3.4/3.5）。</summary>
public sealed class WebFetcher : IWebFetcher
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    static WebFetcher() =>
        Http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (compatible; LogAgent/0.4; +https://localhost)");

    public async Task<string> FetchTextAsync(string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
            throw new InvalidOperationException("仅支持 http/https 地址");
        using var resp = await Http.GetAsync(uri, ct);
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync(ct);
        return ExtractText(html);
    }

    /// <summary>HTML → 正文：去 script/style/nav/header/footer，取文本节点，压缩空白，截断。</summary>
    public static string ExtractText(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var body = doc.DocumentNode.SelectSingleNode("//body") ?? doc.DocumentNode;
        foreach (var xpath in new[] { "//script", "//style", "//nav", "//header", "//footer", "//noscript" })
            foreach (var node in body.SelectNodes(xpath)?.ToArray() ?? [])
                node.Remove();

        var text = HtmlEntity.DeEntitize(body.InnerText);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length > 8000 ? text[..8000] : text;
    }
}
