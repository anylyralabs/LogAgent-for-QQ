using System.Net;
using System.Net.Http;
using System.Text;
using LogAgent.Core;
using LogAgent.Memory;
using LogAgent.Routing;
using LogAgent.Shared.Chat;
using LogAgent.Shared.Configuration;
using LogAgent.Shared.Messaging;
using Xunit;

namespace LogAgent.Tests;

// ─────────────────────────── 消息富化（链接 / 附件） ───────────────────────────

public class MessageEnricherTests : IDisposable
{
    private readonly string _filesDir = Path.Combine(Path.GetTempPath(), $"la-files-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_filesDir, true); } catch (IOException) { }
    }

    private MessageEnricher Make(MiniHttpServer server)
        => new($"http://127.0.0.1:{server.Port}", null, _filesDir, $"http://127.0.0.1:{server.Port}");

    private static BotMessage Msg(string text, IReadOnlyList<MessageAttachment>? attachments = null) => new()
    {
        MessageId = "m1", ChatId = "777", ChatType = ChatType.Group,
        SenderId = "1", SenderName = "柠枺", Text = text, MentionedMe = true,
        Attachments = attachments,
    };

    [Fact]
    public async Task Enrich_NoLinksOrAttachments_Unchanged()
    {
        using var server = new MiniHttpServer((_, _, _) => ("200 OK", "text/plain", "x"));
        var enricher = Make(server);
        var text = await enricher.EnrichTextAsync(Msg("普通消息，没有链接"));
        Assert.Equal("普通消息，没有链接", text);
    }

    [Fact]
    public async Task Enrich_ZipAttachment_IsSavedForShellTools()
    {
        var bytes = Encoding.UTF8.GetBytes("PK\u0003\u0004 fake zip payload");
        using var http = new HttpClient(new BytesHandler(bytes));
        var enricher = new MessageEnricher("http://127.0.0.1:1", http, _filesDir, "http://127.0.0.1:1");

        var text = await enricher.FetchAttachmentAsync(
            new MessageAttachment("crash.zip", "https://example.com/crash.zip", bytes.Length),
            CancellationToken.None);

        Assert.Contains("已完整保存到本机", text);
        Assert.Contains("本机路径：", text);
        Assert.Contains("zipfile", text);
        var pathLine = text.Split('\n').First(l => l.StartsWith("本机路径：", StringComparison.Ordinal));
        var path = pathLine["本机路径：".Length..].Trim();
        Assert.True(File.Exists(path), $"zip 应保留给 shell 解压：{path}");
    }

    private sealed class BytesHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes),
            });
    }

    [Fact]
    public async Task Enrich_UnavailableAttachment_ExplainsReason()
    {
        using var server = new MiniHttpServer((_, _, _) => ("200 OK", "text/plain", "x"));
        var enricher = Make(server);
        var attachments = new List<MessageAttachment>
        {
            new("latest_game.log", "", 3_240_000) { UnavailableReason = "NapCat 的 packetBackend 不可用（NapCat 版本与 QQ 版本不匹配时会出现）" },
        };

        var text = await enricher.EnrichTextAsync(Msg("[文件: latest_game.log]", attachments));
        Assert.Contains("未能读取", text);
        Assert.Contains("packetBackend", text);
        Assert.Contains("latest_game.log", text);
    }

    [Fact]
    public async Task Enrich_LogShareLink_FetchesRawAndInsights()
    {
        using var server = new MiniHttpServer((_, path, _) => path.Contains("/v1/insights/")
            ? ("200 OK", "application/json", """{"id":"vanilla/server","analysis":{"problems":[]}}""")
            : ("200 OK", "text/plain", "[main/ERROR]: java.lang.OutOfMemoryError: Java heap space\n    at a.B(c)"));
        var enricher = Make(server);

        var text = await enricher.EnrichTextAsync(Msg("看看 https://logshare.cn/ABC123 这个"));
        Assert.Contains("【已从 LogShare 链接抓取", text);
        Assert.Contains("java.lang.OutOfMemoryError", text);
        Assert.Contains("结构化解析", text);
        Assert.Contains("https://logshare.cn/ABC123", text); // 原消息保留
    }

    [Fact]
    public async Task Enrich_LogShareLink_Failure_ReportsGracefully()
    {
        using var server = new MiniHttpServer((_, _, _) => ("404 Not Found", "text/plain", "nope"));
        var enricher = Make(server);

        var text = await enricher.EnrichTextAsync(Msg("看这个 https://logshare.cn/ZZZ999"));
        Assert.Contains("抓取失败", text);
    }

    [Fact]
    public async Task Enrich_McLogsLink_FetchesRawAndInsights()
    {
        using var server = new MiniHttpServer((_, path, _) => path.Contains("/1/insights/")
            ? ("200 OK", "application/json", """{"analysis":"Paper 1.21.1","problems":["Mod compat"],"solutions":["Remove mod X"]}""")
            : ("200 OK", "text/plain", "[12:00:00] [main/FATAL]: Failed to initialize game\n    at net.paper.Main.run"));
        var enricher = Make(server);

        // 短链与 api 直链两种形态都应命中
        var text = await enricher.EnrichTextAsync(Msg("mclo.gs 链接：https://mclo.gs/XyZ123abcD48"));
        Assert.Contains("【已从 mclo.gs 链接抓取", text);
        Assert.Contains("Failed to initialize game", text);
        Assert.Contains("结构化解析", text);
        Assert.Contains("https://mclo.gs/XyZ123abcD48", text); // 原消息保留

        var text2 = await enricher.EnrichTextAsync(Msg("再看这个 https://api.mclo.gs/1/raw/XyZ123abcD48"));
        Assert.Contains("【已从 mclo.gs 链接抓取", text2);
        Assert.DoesNotContain("抓取失败", text2);
    }

    [Fact]
    public async Task Enrich_McLogsLink_Failure_ReportsGracefully()
    {
        using var server = new MiniHttpServer((_, _, _) => ("404 Not Found", "text/plain", "nope"));
        var enricher = Make(server);

        var text = await enricher.EnrichTextAsync(Msg("https://mclo.gs/QQQ999aaa111"));
        Assert.Contains("抓取失败", text);
        Assert.Contains("mclo.gs", text);
    }

    [Fact]
    public async Task Enrich_TextAttachment_DownloadsAndReads()
    {
        using var server = new MiniHttpServer((_, _, _) => ("200 OK", "text/plain",
            "[17:00:00] [main/ERROR]: fcl crash test\n    at x.y.Z(z.java:1)"));
        var enricher = Make(server);

        var text = await enricher.EnrichTextAsync(Msg("", [new MessageAttachment("fcl.log", $"http://127.0.0.1:{server.Port}/f", 40)]));
        Assert.Contains("【QQ 文件「fcl.log」", text);
        Assert.Contains("fcl crash test", text);
    }

    [Fact]
    public async Task Enrich_BinaryAttachment_NotRead()
    {
        using var server = new MiniHttpServer((_, _, _) => ("200 OK", "application/octet-stream",
            "PK\u0003\u0004binary\u0000content"));
        var enricher = Make(server);

        var text = await enricher.EnrichTextAsync(Msg("", [new MessageAttachment("logs.bin", $"http://127.0.0.1:{server.Port}/z", 20)]));
        Assert.Contains("不是可读文本", text);
    }

    [Fact]
    public async Task Enrich_OversizeAttachment_Skipped()
    {
        using var server = new MiniHttpServer((_, _, _) => ("200 OK", "text/plain", "x"));
        var enricher = Make(server);

        // 300MB > 128MB 上限 → 不下载，给引导提示
        var text = await enricher.EnrichTextAsync(Msg("", [new MessageAttachment("big.zip.log", $"http://127.0.0.1:{server.Port}/b", 300 * 1024 * 1024)]));
        Assert.Contains("上限", text);
        Assert.Contains("logshare.cn", text);
    }

    [Fact]
    public async Task Enrich_LongContent_StagedWithToolsHint()
    {
        var body = "HEAD" + new string('x', 20000) + "TAIL";
        using var server = new MiniHttpServer((_, _, _) => ("200 OK", "text/plain", body));
        var enricher = Make(server);
        var text = await enricher.EnrichTextAsync(Msg("", [new MessageAttachment("long.log", $"http://127.0.0.1:{server.Port}/l", body.Length)]));

        // 片段 + 路径 + 工具指引（中间内容不丢的设计）
        Assert.Contains("已完整保存", text);
        Assert.Contains("HEAD", text);
        Assert.Contains("TAIL", text);
        Assert.Contains("search_file", text);
        Assert.Contains("read_file", text);
        Assert.Contains("本机路径：", text);

        // 完整文件落盘，内容与原文一致（中间不省略）
        var staged = Directory.GetFiles(_filesDir).Single();
        var content = File.ReadAllText(staged);
        Assert.Equal(body.Length, content.Length);
        Assert.Contains("TAIL", content);
    }

    [Fact]
    public async Task Enrich_LogShareLink_LongRaw_StagedFullText()
    {
        var raw = new string('a', 5000) + "MIDDLE-MARKER" + new string('b', 12000);
        using var server = new MiniHttpServer((_, path, _) => path.Contains("/v1/insights/")
            ? ("200 OK", "application/json", """{"id":"x","ok":true}""")
            : ("200 OK", "text/plain", raw));
        var enricher = Make(server);
        var text = await enricher.EnrichTextAsync(Msg("看下 https://logshare.cn/ABC123"));

        Assert.Contains("抓取全文并保存", text);
        Assert.Contains("本机路径：", text);
        Assert.Contains("search_file", text);

        var staged = Directory.GetFiles(_filesDir).Single();
        Assert.Contains("MIDDLE-MARKER", File.ReadAllText(staged)); // 中间内容完整落盘
    }
}

// ─────────────────────────── 图片（多模态） ───────────────────────────

public class VisionSupportTests
{
    [Fact]
    public void ExtractImageUrls_ReadsNapCatUrl()
    {
        var urls = LogAgent.Connections.OneBot11Connection.ExtractImageUrls("""
            {"message":[
                {"type":"text","data":{"text":"看这个"}},
                {"type":"image","data":{"file":"a.jpg","url":"https://multimedia.nt.qq.com.cn/download?x=1","file_size":118484}}
            ]}
            """);
        Assert.Single(urls);
        Assert.StartsWith("https://multimedia.nt.qq.com.cn", urls[0]);
    }

    [Fact]
    public void ExtractImageUrls_NoUrl_Skipped()
    {
        var urls = LogAgent.Connections.OneBot11Connection.ExtractImageUrls("""
            {"message":[{"type":"image","data":{"file":"a.jpg"}}]}
            """);
        Assert.Empty(urls);
    }

    [Fact]
    public void GuessMime_ByMagicBytes()
    {
        Assert.Equal("image/jpeg", LogAgent.Core.ImageFetcher.GuessMime([0xFF, 0xD8, 0xFF]));
        Assert.Equal("image/png", LogAgent.Core.ImageFetcher.GuessMime([0x89, 0x50, 0x4E, 0x47]));
        Assert.Equal("image/gif", LogAgent.Core.ImageFetcher.GuessMime([0x47, 0x49, 0x46]));
    }

    [Fact]
    public async Task Serialize_ImageTurn_ProducesContentArray()
    {
        var handler = new CaptureHandler();
        var client = new LogAgent.Routing.OpenAiCompatClient(
            new LogAgent.Shared.Configuration.ProviderConfig { Id = "p", ChatModel = "m" },
            new System.Net.Http.HttpClient(handler));
        var turn = LogAgent.Shared.Chat.ChatTurn.UserWithImages("看看这张图", ["data:image/jpeg;base64,AAAA"]);
        await client.CompleteAsync(new LogAgent.Routing.ChatRequest([turn], 100, 0));

        var body = handler.LastRequestBody ?? "";
        Assert.Contains("\"type\":\"image_url\"", body);
        Assert.Contains("data:image/jpeg;base64,AAAA", body);
        Assert.Contains("看看这张图", body);
    }

    [Fact]
    public async Task Agent_MessageWithImages_AttachesMultimodalTurn()
    {
        var appConfig = new AppConfig
        {
            Providers = [new ProviderConfig { Id = "p1", Name = "P1", ChatModel = "m", Enabled = true }],
            Rules = [new RouterRule { TaskType = TaskType.Chat, ProviderChain = ["p1"] }],
        };
        FakeClient? client = null;
        var router = new ModelRouter(appConfig, _ => client = new FakeClient("p1", true));
        var fetcher = new LogAgent.Core.ImageFetcher(new System.Net.Http.HttpClient(new TinyImageHandler()));
        var agent = new LogAgent.Core.GroupContextAgent(router, null, null, null, null, fetcher);

        var message = new BotMessage
        {
            MessageId = "m1", ChatId = "777", ChatType = ChatType.Group,
            SenderId = "1", SenderName = "群友", Text = "看看这个报错", MentionedMe = true,
            ImageUrls = ["https://example.test/shot.jpg"],
        };
        var window = new List<ChatTurn> { ChatTurn.User("群友: 看看这个报错") };
        await agent.ReplyAsync(message, new SessionContext("s", "c", [], window));

        var request = client!.LastRequest!;
        var lastUser = request.Messages.Last(m => m.Role == "user");
        Assert.NotNull(lastUser.ImageDataUrls);
        Assert.Single(lastUser.ImageDataUrls!);
        Assert.StartsWith("data:image/png;base64,", lastUser.ImageDataUrls![0]);
        Assert.Contains("看看这个报错", lastUser.Content); // 原文本保留
    }
}

/// <summary>返回一张最小 PNG 的 HTTP 处理器（图片下载测试用）。</summary>
internal sealed class TinyImageHandler : System.Net.Http.HttpMessageHandler
{
    protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
        System.Net.Http.HttpRequestMessage request, CancellationToken ct)
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01, 0x02 };
        var resp = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new System.Net.Http.ByteArrayContent(png),
        };
        resp.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        return Task.FromResult(resp);
    }
}

// ─────────────────────────── 连接层：文件段解析 ───────────────────────────

public class OneBotFileSegmentTests
{
    [Fact]
    public void ExtractFileSegments_ReadsNapCatFields()
    {
        var segments = LogAgent.Connections.OneBot11Connection.ExtractFileSegments("""
            {"message":[
                {"type":"at","data":{"qq":"3990639355"}},
                {"type":"text","data":{"text":" 日志来了"}},
                {"type":"file","data":{"file":"fcl-exported-logs-2026-10-15.log","file_id":"abc123","file_size":20480,"busid":7}}
            ]}
            """);
        var seg = Assert.Single(segments);
        Assert.Equal("fcl-exported-logs-2026-10-15.log", seg.Name);
        Assert.Equal("abc123", seg.FileId);
        Assert.Equal(20480, seg.Size);
        Assert.Equal(7, seg.Busid);
        Assert.Null(seg.DirectUrl);
    }

    [Fact]
    public void ExtractFileSegments_StringFields_AndDirectUrl()
    {
        // get_msg 历史消息：file_size 为字符串、直接带 url（兼容解析）
        var segments = LogAgent.Connections.OneBot11Connection.ExtractFileSegments("""
            {"message":[
                {"type":"file","data":{"file":"latest_game.log","file_id":"/2ff7313b-3319","file_size":"29604093","url":"https://example.com/f"}}
            ]}
            """);
        var seg = Assert.Single(segments);
        Assert.Equal(29604093, seg.Size);
        Assert.Equal("https://example.com/f", seg.DirectUrl);
    }

    [Fact]
    public void ExtractPlainText_ShowsFileNameForFileSegment()
    {
        var text = LogAgent.Connections.OneBot11Connection.ExtractPlainText("""
            {"message":[
                {"type":"text","data":{"text":"看下"}},
                {"type":"file","data":{"file":"latest.log","file_id":"x"}}
            ]}
            """);
        Assert.Contains("[文件: latest.log]", text);
        Assert.Contains("看下", text);
    }

    [Fact]
    public void SummarizeFileApiError_ExtractsPacketBackendHint()
    {
        var summary = LogAgent.Connections.OneBot11Connection.SummarizeFileApiError(
            "packetBackend不可用，请参照文档 https://napneko.github.io/config/advanced 检查packetBackend状态或进行配置！");
        Assert.Contains("packetBackend", summary);
        Assert.Contains("无法获取", summary);

        var fallback = LogAgent.Connections.OneBot11Connection.SummarizeFileApiError(null);
        Assert.Contains("未返回", fallback);
    }

    [Fact]
    public void ExtractReplyMessageId_FindsIdOrNull()
    {
        var id = LogAgent.Connections.OneBot11Connection.ExtractReplyMessageId("""
            {"message":[{"type":"reply","data":{"id":"123456"}},{"type":"text","data":{"text":"看下"}}]}
            """);
        Assert.Equal("123456", id);

        var numId = LogAgent.Connections.OneBot11Connection.ExtractReplyMessageId("""
            {"message":[{"type":"reply","data":{"id":998877}}]}
            """);
        Assert.Equal("998877", numId);

        Assert.Null(LogAgent.Connections.OneBot11Connection.ExtractReplyMessageId("""
            {"message":[{"type":"text","data":{"text":"x"}}]}
            """));
    }
}

// ─────────────────────────── 连接层：官方 Bot 附件解析 ───────────────────────────

public class OfficialBotAttachmentTests
{
    [Fact]
    public void ParseAttachments_ReadsUrlAndFilename()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""
            {"content":"",
             "attachments":[
                {"content_type":"file","filename":"crash-2026-10-15.txt","url":"https://example.com/a","size":1234},
                {"content_type":"image","url":"https://example.com/b"}
             ]}
            """);
        var attachments = LogAgent.Connections.OfficialBotConnection.ParseAttachments(doc.RootElement);
        Assert.Equal(2, attachments.Count);
        Assert.Equal("crash-2026-10-15.txt", attachments[0].Name);
        Assert.Equal("https://example.com/a", attachments[0].Url);
        Assert.Equal(1234, attachments[0].Size);
        Assert.Equal("附件-image", attachments[1].Name);
    }

    [Fact]
    public void ParseAttachments_NoAttachments_ReturnsEmpty()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""{"content":"hi"}""");
        Assert.Empty(LogAgent.Connections.OfficialBotConnection.ParseAttachments(doc.RootElement));
    }
}

// ─────────────────────────── 运行时：链接富化进入管线 ───────────────────────────

public class RuntimeEnrichmentTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"la-enrich-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch (IOException) { }
    }

    [Fact]
    public async Task LinkMessage_FetchedContent_ReachesModel()
    {
        using var server = new MiniHttpServer((_, path, _) => path.Contains("/v1/insights/")
            ? ("200 OK", "application/json", "{}")
            : ("200 OK", "text/plain", "[main/ERROR]: SIGSEGV crash at Native.ctx"));
        var enricher = new MessageEnricher($"http://127.0.0.1:{server.Port}");

        var appConfig = new AppConfig
        {
            Providers = [new ProviderConfig { Id = "p1", Name = "P1", ChatModel = "m", Enabled = true }],
            Rules = [new RouterRule { TaskType = TaskType.Chat, ProviderChain = ["p1"] }],
        };
        var inputs = new List<string>();
        var router = new ModelRouter(appConfig, _ => new FakeClient("p1", true)
        {
            OnInput = inputs.Add,
            ReplySelector = _ => "收到，正在看",
        });
        var db = new Database(_dbPath);
        db.EnsureCreated();
        var sessions = new SessionManager(new SessionStore(db), router, new AgentConfig());
        var runtime = new AgentRuntime(
            router,
            new GroupContextAgent(router),
            new LogAnalysisAgent(router),
            null,
            sessions,
            new AgentConfig(),
            enricher);

        var message = new BotMessage
        {
            MessageId = "m9", ChatId = "888", ChatType = ChatType.Group,
            SenderId = "1", SenderName = "柠枺",
            Text = "帮我看看 https://logshare.cn/J8x9Flt 谢谢", MentionedMe = true,
        };
        var bridge = new RecordingBridge();
        await runtime.OnMessageAsync(bridge, message);

        var modelInput = Assert.Single(inputs);
        Assert.Contains("【已从 LogShare 链接抓取", modelInput); // 富化正文进入模型上下文
        Assert.Contains("SIGSEGV crash", modelInput);
        Assert.Single(bridge.Sent); // 回复正常发出
    }
}
