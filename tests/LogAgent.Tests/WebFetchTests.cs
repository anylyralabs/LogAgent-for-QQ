using System.Net;
using LogAgent.Core;
using LogAgent.Knowledge;
using LogAgent.Memory;
using LogAgent.Routing;
using LogAgent.Shared.Chat;
using LogAgent.Shared.Configuration;
using LogAgent.Shared.Messaging;
using LogAgent.Skills;
using Xunit;

namespace LogAgent.Tests;

// ─────────────────────────── 网站浏览能力（守卫 / 抓取 / 工具 / 富化） ───────────────────────────

public class WebFetchTests : IDisposable
{
    private readonly string _filesDir = Path.Combine(Path.GetTempPath(), $"la-web-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_filesDir, true); } catch (IOException) { }
    }

    // ---------- SSRF 守卫（纯函数） ----------

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.9")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.9.9")]
    [InlineData("100.64.0.7")]
    public void Guard_BlocksPrivateAddresses(string ip)
    {
        Assert.True(WebFetchCore.IsBlockedAddresses([IPAddress.Parse(ip)]));
    }

    [Fact]
    public void Guard_AllowsPublicAddress()
    {
        Assert.False(WebFetchCore.IsBlockedAddresses([IPAddress.Parse("93.184.216.34")]));
        Assert.False(WebFetchCore.IsBlockedAddresses([IPAddress.Parse("2606:2800:220:1:248:1893:25c8:1946")]));
    }

    // ---------- HTML 提取 ----------

    [Fact]
    public void Extract_TitleAndBody_StripsNoise()
    {
        var html = """
            <html><head><title>安装指南</title></head><body>
            <nav>导航 导航</nav>
            <script>var evil = 1;</script>
            <h1>步骤</h1><p>先下载  后安装。</p>
            <footer>版权所有</footer>
            </body></html>
            """;
        var page = WebFetchCore.Extract("https://example.com/guide", html, "text/html", 100, 8000);
        Assert.Equal("安装指南", page.Title);
        Assert.DoesNotContain("导航 导航", page.Text);
        Assert.DoesNotContain("var evil", page.Text);
        Assert.DoesNotContain("版权所有", page.Text);
        Assert.Contains("先下载 后安装", page.Text);
    }

    // ---------- 抓取（本地假服务器，allowPrivate 跳过守卫） ----------

    [Fact]
    public async Task FetchAsync_ParsesPage()
    {
        using var server = new MiniHttpServer((_, path, _) => path.Contains("/doc")
            ? ("200 OK", "text/html", "<html><head><title>文档页</title></head><body><p>正文内容 ABC</p></body></html>")
            : ("404 Not Found", "text/plain", "nope"));

        var page = await WebFetchCore.FetchAsync($"http://127.0.0.1:{server.Port}/doc", allowPrivate: true);
        Assert.Equal("文档页", page.Title);
        Assert.Contains("正文内容 ABC", page.Text);
        Assert.False(page.Truncated);
    }

    [Fact]
    public async Task FetchAsync_RejectsNonTextContent()
    {
        using var server = new MiniHttpServer((_, _, _) => ("200 OK", "image/png", "PNGDATA"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => WebFetchCore.FetchAsync($"http://127.0.0.1:{server.Port}/img", allowPrivate: true));
    }

    [Fact]
    public async Task FetchAsync_TruncatesLongText()
    {
        using var server = new MiniHttpServer((_, _, _) => ("200 OK", "text/plain", new string('x', 500)));
        var page = await WebFetchCore.FetchAsync($"http://127.0.0.1:{server.Port}/long", allowPrivate: true, maxTextChars: 100);
        Assert.True(page.Truncated);
        Assert.Equal(100, page.Text.Length);
    }

    // ---------- web_fetch 工具 ----------

    [Fact]
    public async Task WebTools_ExecutesAndRejectsPrivateByDefault()
    {
        var tools = new WebTools();
        using var server = new MiniHttpServer((_, _, _) => ("200 OK", "text/html",
            "<html><head><title>工具页</title></head><body><p>工具测试正文</p></body></html>"));

        var ok = await tools.ExecuteAsync("web_fetch", $$"""{"url":"http://127.0.0.1:{{server.Port}}/t","max_chars":100}""");
        Assert.Contains("拒绝访问", ok);

        var permissive = new WebTools(allowPrivate: true);
        var text = await permissive.ExecuteAsync("web_fetch", $$"""{"url":"http://127.0.0.1:{{server.Port}}/t"}""");
        Assert.Contains("【网页：工具页】", text);
        Assert.Contains("工具测试正文", text);
    }

    // ---------- 富化：消息里的网页链接自动抓取 ----------

    private static BotMessage Msg(string text) => new()
    {
        MessageId = "m1", ChatId = "777", ChatType = ChatType.Group,
        SenderId = "1", SenderName = "柠枺", Text = text, MentionedMe = true,
    };

    [Fact]
    public async Task Enrich_WebLink_FetchesPage()
    {
        using var server = new MiniHttpServer((_, path, _) => path.Contains("/post")
            ? ("200 OK", "text/html", "<html><head><title>帖子标题</title></head><body><p>论坛正文内容 XYZ</p></body></html>")
            : ("404 Not Found", "text/plain", "nope"));
        var baseUri = $"http://127.0.0.1:{server.Port}";
        var enricher = new MessageEnricher(baseUri, null, _filesDir, mcLogsBase: baseUri);

        // allowPrivate 默认 false——本机地址会被守卫拒绝，验证失败路径文案
        var text = await enricher.EnrichTextAsync(Msg($"看看这个 {baseUri}/post"));
        Assert.Contains("网页抓取失败", text);
        Assert.Contains("拒绝访问本机/内网地址", text);

        // allowPrivate 开启时走成功路径
        var permissive = new MessageEnricher(baseUri, null, _filesDir, mcLogsBase: baseUri, allowPrivateWebFetch: true);
        var ok = await permissive.EnrichTextAsync(Msg($"看看这个 {baseUri}/post"));
        Assert.Contains("【已抓取网页：帖子标题】", ok);
        Assert.Contains("论坛正文内容 XYZ", ok);
    }
}
