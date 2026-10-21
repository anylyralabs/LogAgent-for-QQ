using System.Net;
using System.Text;
using System.Text.Json;
using LogAgent.Connections;
using LogAgent.Core;
using LogAgent.Shared;
using LogAgent.Shared.Configuration;
using LogAgent.Shared.Connections;
using LogAgent.Shared.Messaging;
using Xunit;

namespace LogAgent.Tests;

// ─────────────────────────── 频道抽象（OpenClaw channel 形态） ───────────────────────────

public class ChannelConfigTests
{
    [Fact]
    public void Migration_CreatesOfficialAndOneBotChannels()
    {
        var config = new AppConfig();
#pragma warning disable CS0618 // 迁移测试必须写旧字段
        config.OneBot11 = new OneBot11Config { Enabled = true, Port = 6702, Token = "ob" };
        config.OfficialBot = new OfficialBotConfig
        {
            Enabled = true, AppId = "app", AppSecret = "secret", Sandbox = true,
        };
#pragma warning restore CS0618

        config.NormalizeChannels();

        Assert.NotNull(config.Channels);
        Assert.Equal(2, config.Channels!.Count);
        var official = Assert.Single(config.Channels, c => c.Transport == ChannelTransport.Official);
        var oneBot = Assert.Single(config.Channels, c => c.Transport == ChannelTransport.OneBot11);

        Assert.Equal("app", official.ResolveOfficialCredentials().AppId);
        Assert.Equal("secret", official.ResolveOfficialCredentials().AppSecret);
        Assert.True(official.ResolveSandbox());
        Assert.Equal(100, official.Priority);
        Assert.False(official.Capabilities.Forward);
        Assert.True(official.Capabilities.Merged);

        Assert.True(oneBot.Enabled);
        Assert.Equal(6702, oneBot.OneBot11.Port);
        Assert.Equal("ob", oneBot.OneBot11.Token);
        Assert.Equal(50, oneBot.Priority);
        Assert.True(oneBot.Capabilities.Forward);
        Assert.False(oneBot.Capabilities.Merged);

        // 官方 Bot 优先：即使配置顺序反过来，ActiveChannels 也先返回官方
        Assert.Equal(ChannelTransport.Official, config.ActiveChannels()[0].Transport);
    }

    [Fact]
    public void ExplicitEmptyChannels_DoesNotResurrectLegacyFields()
    {
        var config = new AppConfig
        {
            Channels = [],
        };
#pragma warning disable CS0618
        config.OfficialBot = new OfficialBotConfig { Enabled = true, AppId = "a", AppSecret = "b" };
#pragma warning restore CS0618

        config.NormalizeChannels();

        Assert.Empty(config.Channels!);
    }

    [Fact]
    public void EffectiveSnapshots_AreReadOnlyViews()
    {
        var config = AppConfig.CreateDefault();
        var channel = config.GetOrAddChannel(ChannelTransport.Official);
        channel.SetOfficialCredentials("app", "secret", sandbox: false);

        var snapshot = config.EffectiveOfficialBot();
        snapshot.AppId = "mutated";
        snapshot.AppSecret = "mutated";

        Assert.Equal("app", channel.ResolveOfficialCredentials().AppId);
        Assert.Equal("secret", channel.ResolveOfficialCredentials().AppSecret);
    }

    [Fact]
    public void GetOrAddChannel_AddsMissingTransport()
    {
        var config = AppConfig.CreateDefault();
        config.Channels = [AppConfig.CreateOfficialChannel()];

        var oneBot = config.GetOrAddChannel(ChannelTransport.OneBot11);

        Assert.Equal(ChannelTransport.OneBot11, oneBot.Transport);
        Assert.Contains(oneBot, config.Channels);
    }

    [Fact]
    public void Fingerprint_ChangesWhenMarkdownOrMergeChanges()
    {
        var channels = AppConfig.CreateDefault().Channels!;
        var before = channels.ChannelFingerprint();
        channels[0].MarkdownSupport = !channels[0].MarkdownSupport;
        var afterMarkdown = channels.ChannelFingerprint();
        channels[0].MergeMessages = !channels[0].MergeMessages;
        var afterMerge = channels.ChannelFingerprint();

        Assert.NotEqual(before, afterMarkdown);
        Assert.NotEqual(afterMarkdown, afterMerge);
    }
}

public class PromptFormatTests
{
    [Fact]
    public void MarkdownPrompt_DoesNotUseChineseBrackets()
    {
        var prompt = Prompts.XiaoyanFor(markdown: true);

        Assert.Contains("支持 Markdown", prompt);
        Assert.Contains("表格", prompt);
        Assert.Contains("3 列", prompt);
        Assert.DoesNotContain("QQ 不渲染 Markdown", prompt);
        Assert.DoesNotContain("【结论】", prompt);

        var analysis = Prompts.LogAnalysisRole(markdown: true);
        Assert.Contains("## 结论", analysis);
        Assert.Contains("表格", analysis);
        Assert.DoesNotContain("【结论】", analysis);
    }

    [Fact]
    public void ChatContextLine_DistinguishesGroupAndPrivate()
    {
        Assert.Contains("QQ 群聊", Prompts.ChatContextLine(ChatType.Group, "qq-official"));
        Assert.Contains("QQ 私聊", Prompts.ChatContextLine(ChatType.Private, "qq-official"));
        Assert.Contains("不要引用其他群", Prompts.ChatContextLine(ChatType.Group, "qq-official"));
    }

    [Fact]
    public void SplitSections_RecognizesMarkdownHeadings()
    {
        var sections = AgentRuntime.SplitSections("## 结论\nA\n## 原因分析\nB");

        Assert.Equal(2, sections.Count);
        Assert.StartsWith("## 结论", sections[0]);
        Assert.StartsWith("## 原因分析", sections[1]);
    }
}

public class ChannelManagerTests
{
    [Fact]
    public async Task StartAll_StartsOfficialFirst_AndRoutesReplyToOriginChannel()
    {
        var official = new ChannelConfig
        {
            Id = "qq-official", Name = "官方", Transport = ChannelTransport.Official,
            Enabled = true, Priority = 100, AppId = "a", AppSecret = "b",
        };
        var oneBot = new ChannelConfig
        {
            Id = "qq-onebot", Name = "OneBot", Transport = ChannelTransport.OneBot11,
            Enabled = true, Priority = 50, OneBot11 = new OneBot11Config { Port = 6701 },
        };
        var started = new List<string>();
        var fakes = new Dictionary<string, FakeConnection>
        {
            [official.Id] = new FakeConnection("official"),
            [oneBot.Id] = new FakeConnection("onebot"),
        };

        await using var manager = new ChannelManager([oneBot, official], c =>
        {
            started.Add(c.Id);
            return fakes[c.Id];
        });

        Assert.True(await manager.StartAllAsync());
        Assert.Equal(new[] { official.Id, oneBot.Id }, started);

        await manager.CreateSender(oneBot.Id).SendTextAsync("123", ChatType.Group, "回 OneBot", "m1");
        await manager.CreateSender(official.Id).SendTextAsync("openid", ChatType.Group, "回官方", "m2");

        Assert.DoesNotContain(fakes[official.Id].Sent, s => s.Text == "回 OneBot");
        Assert.DoesNotContain(fakes[oneBot.Id].Sent, s => s.Text == "回官方");
        Assert.Contains(fakes[oneBot.Id].Sent, s => s.Text == "回 OneBot" && s.ReplyToMessageId == "m1");
        Assert.Contains(fakes[official.Id].Sent, s => s.Text == "回官方" && s.ReplyToMessageId == "m2");

        // 主动发送默认选优先级最高且已连接的频道（官方 Bot）
        await manager.PreferredSender!.SendProactiveAsync("openid", ChatType.Group, "主动");
        Assert.Contains(fakes[official.Id].Sent, s => s.Text == "主动" && s.ReplyToMessageId is null);
    }

    [Fact]
    public async Task StartAll_ContinuesWhenOneChannelFails()
    {
        var bad = new ChannelConfig
        {
            Id = "bad", Name = "坏频道", Transport = ChannelTransport.Official,
            Enabled = true, Priority = 100, AppId = "a", AppSecret = "b",
        };
        var good = new ChannelConfig
        {
            Id = "good", Name = "好频道", Transport = ChannelTransport.OneBot11,
            Enabled = true, Priority = 50, OneBot11 = new OneBot11Config { Port = 6701 },
        };
        var fake = new FakeConnection("good");
        await using var manager = new ChannelManager([bad, good], c =>
            c.Id == "bad" ? throw new InvalidOperationException("boom") : fake);

        Assert.False(await manager.StartAllAsync());
        Assert.Equal("boom", manager.Find(bad.Id)!.LastError);
        Assert.True(manager.Find(good.Id)!.Connected);
    }

    [Fact]
    public async Task InboundMerge_CombinesBurstMessagesIntoSingleTurn()
    {
        var channel = new ChannelConfig
        {
            Id = "qq-official", Name = "官方", Transport = ChannelTransport.Official,
            Enabled = true, Priority = 100, AppId = "a", AppSecret = "b",
            MergeMessages = true, MergeWindowMs = 40,
            Capabilities = new ChannelCapabilities { Merged = true },
        };
        var fake = new FakeConnection("official");
        await using var manager = new ChannelManager([channel], _ => fake);
        await manager.StartAllAsync();

        var received = new TaskCompletionSource<ChannelMessageEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.MessageReceived += (_, e) =>
        {
            if (e.MergedCount > 1) received.TrySetResult(e);
        };

        fake.Emit(Message("第一条", "u1", "小明"));
        fake.Emit(Message("第二条", "u2", "小红"));
        fake.Emit(Message("第三条", "u3", "小刚"));

        var merged = await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(3, merged.MergedCount);
        Assert.Contains("第一条", merged.Message.Text);
        Assert.Contains("第三条", merged.Message.Text);
        Assert.Contains("[当前消息", merged.Message.Text);
        Assert.Equal("u3", merged.Message.SenderId);
    }

    [Fact]
    public async Task OfficialChannel_MarksMessagesWithMarkdownAndChannel()
    {
        var channel = new ChannelConfig
        {
            Id = "qq-official", Name = "官方", Transport = ChannelTransport.Official,
            Enabled = true, Priority = 100, AppId = "a", AppSecret = "b",
            MergeMessages = false,
            Capabilities = new ChannelCapabilities { Markdown = true, Merged = true },
        };
        var fake = new FakeConnection("official");
        await using var manager = new ChannelManager([channel], _ => fake);
        await manager.StartAllAsync();

        var received = new TaskCompletionSource<ChannelMessageEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.MessageReceived += (_, e) => received.TrySetResult(e);
        fake.Emit(Message("你好", "u1", "小明"));

        var evt = await received.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(evt.Message.SupportsMarkdown);
        Assert.Equal(channel.Id, evt.Message.ChannelId);
        Assert.Equal(channel.Name, evt.Message.ChannelName);
    }

    [Fact]
    public async Task InboundMerge_CommandIsHandledImmediately()
    {
        var channel = new ChannelConfig
        {
            Id = "qq-official", Name = "官方", Transport = ChannelTransport.Official,
            Enabled = true, Priority = 100, AppId = "a", AppSecret = "b",
            MergeMessages = true, MergeWindowMs = 500,
        };
        var fake = new FakeConnection("official");
        await using var manager = new ChannelManager([channel], _ => fake);
        await manager.StartAllAsync();

        var received = new TaskCompletionSource<ChannelMessageEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.MessageReceived += (_, e) => received.TrySetResult(e);
        fake.Emit(Message("/new", "u1", "小明"));

        var evt = await received.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, evt.MergedCount);
        Assert.Equal("/new", evt.Message.Text);
    }

    [Fact]
    public async Task MergedSend_UsesSanitizedCapabilityGate()
    {
        var channel = new ChannelConfig
        {
            Id = "qq-official", Name = "官方", Transport = ChannelTransport.Official,
            Enabled = true, Priority = 100, AppId = "a", AppSecret = "b",
            Capabilities = new ChannelCapabilities { Merged = true },
        };
        var fake = new FakeConnection("official") { ForwardSupported = true };
        await using var manager = new ChannelManager([channel], _ => fake);
        await manager.StartAllAsync();

        var ok = await manager.CreateSender(channel.Id).TrySendForwardAsync(
            "openid", ChatType.Group, ["【结论】A", "【原因】B"], "m1");

        Assert.True(ok);
        Assert.Contains(fake.Sent, s => s.Text.Contains("【结论】A") && s.ReplyToMessageId == "m1");
    }

    private static BotMessage Message(string text, string senderId, string senderName) => new()
    {
        MessageId = Guid.NewGuid().ToString("N"),
        ChatId = "group-openid",
        ChatType = ChatType.Group,
        SenderId = senderId,
        SenderName = senderName,
        Text = text,
        MentionedMe = true,
    };
}

public class OfficialBotSendTests
{
    [Fact]
    public async Task ParseEvent_UsesInnerMessageId()
    {
        using var doc = JsonDocument.Parse("""
        {
          "id": "outer-event-id",
          "t": "GROUP_AT_MESSAGE_CREATE",
          "d": {
            "id": "inner-msg-id",
            "content": "帮我看看日志",
            "group_openid": "group-1",
            "timestamp": "2026-07-21T10:00:00+08:00",
            "author": { "member_openid": "user-1", "username": "小明" }
          }
        }
        """);
        await using var conn = new OfficialBotConnection(new OfficialBotConfig { AppId = "a", AppSecret = "b" });

        var msg = conn.ParseEvent(doc.RootElement, isGroup: true);

        Assert.NotNull(msg);
        Assert.Equal("inner-msg-id", msg!.MessageId);
        Assert.Equal("group-1", msg.ChatId);
        Assert.Equal("user-1", msg.SenderId);
        Assert.Equal("小明", msg.SenderName);
        Assert.Equal(ChatType.Group, msg.ChatType);
        Assert.True(msg.MentionedMe);
    }

    [Fact]
    public async Task ParseEvent_FullGroupMessage_IsContextOnly()
    {
        using var doc = JsonDocument.Parse("""
        { "d": { "id": "m1", "content": "大家早上好", "group_openid": "g1",
                 "author": { "member_openid": "u1", "username": "小明" } } }
        """);
        await using var conn = new OfficialBotConnection(new OfficialBotConfig { AppId = "a", AppSecret = "b" });

        var msg = conn.ParseEvent(doc.RootElement, isGroup: true, mentionedMe: false);

        Assert.NotNull(msg);
        Assert.False(msg!.MentionedMe);
        Assert.Equal("大家早上好", msg.Text);
    }

    [Fact]
    public async Task ParseEvent_FullGroupMessage_DetectsBotMention()
    {
        using var doc = JsonDocument.Parse("""
        { "d": { "id": "m1", "content": "<@BOT_OPENID> 你好", "group_openid": "g1",
                 "mentions": [],
                 "author": { "member_openid": "u1", "username": "小明" } } }
        """);
        await using var conn = new OfficialBotConnection(new OfficialBotConfig { AppId = "a", AppSecret = "b" });

        var msg = conn.ParseEvent(doc.RootElement, isGroup: true, mentionedMe: false);

        Assert.NotNull(msg);
        Assert.True(msg!.MentionedMe);
    }

    [Fact]
    public async Task ParseEvent_FullGroupMessage_UsesIsYouFlag()
    {
        using var doc = JsonDocument.Parse("""
        { "d": { "id": "m1", "content": "<@BOT_OPENID> 你好", "group_openid": "g1",
                 "mentions": [{ "id": "BOT_OPENID", "is_you": true }],
                 "author": { "member_openid": "u1", "username": "小明" } } }
        """);
        await using var conn = new OfficialBotConnection(new OfficialBotConfig { AppId = "a", AppSecret = "b" });

        var msg = conn.ParseEvent(doc.RootElement, isGroup: true, mentionedMe: false);

        Assert.NotNull(msg);
        Assert.True(msg!.MentionedMe);
    }

    [Fact]
    public async Task ParseEvent_FullGroupMessage_IgnoresMentionOfOthers()
    {
        using var doc = JsonDocument.Parse("""
        { "d": { "id": "m1", "content": "<@OTHER_USER> 你好", "group_openid": "g1",
                 "mentions": [{ "id": "OTHER_USER" }],
                 "author": { "member_openid": "u1", "username": "小明" } } }
        """);
        await using var conn = new OfficialBotConnection(new OfficialBotConfig { AppId = "a", AppSecret = "b" });

        var msg = conn.ParseEvent(doc.RootElement, isGroup: true, mentionedMe: false);

        Assert.NotNull(msg);
        Assert.False(msg!.MentionedMe);
    }

    [Fact]
    public async Task ParseEvent_C2C_UsesAuthorOpenIdAsChatId()
    {
        using var doc = JsonDocument.Parse("""
        {
          "id": "outer-event-id",
          "t": "C2C_MESSAGE_CREATE",
          "d": {
            "id": "c2c-msg-id",
            "content": "你好",
            "author": { "user_openid": "user-openid", "username": "小李" }
          }
        }
        """);
        await using var conn = new OfficialBotConnection(new OfficialBotConfig { AppId = "a", AppSecret = "b" });

        var msg = conn.ParseEvent(doc.RootElement, isGroup: false);

        Assert.NotNull(msg);
        Assert.Equal("c2c-msg-id", msg!.MessageId);
        Assert.Equal("user-openid", msg.ChatId);
        Assert.Equal("user-openid", msg.SenderId);
        Assert.Equal(ChatType.Private, msg.ChatType);
    }

    [Fact]
    public async Task MarkdownEnabled_SendsMsgType2_AndOmitsContent()
    {
        var handler = new RecordingHandler((_, _) => Json(HttpStatusCode.OK, """{"id":"m1"}"""));
        using var http = new HttpClient(handler);
        await using var conn = new OfficialBotConnection(
            new OfficialBotConfig { AppId = "a", AppSecret = "b", MarkdownSupport = true }, http);

        await conn.SendTextAsync("group-openid", ChatType.Group, "**结论**：没事", "msg-1");

        var request = Assert.Single(handler.Requests);
        using var doc = JsonDocument.Parse(request.Body);
        Assert.Equal(2, doc.RootElement.GetProperty("msg_type").GetInt32());
        Assert.Equal("**结论**：没事", doc.RootElement.GetProperty("markdown").GetProperty("content").GetString());
        Assert.False(doc.RootElement.TryGetProperty("content", out _));
        Assert.Equal("msg-1", doc.RootElement.GetProperty("msg_id").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("msg_seq").GetInt32());
        Assert.Equal("/v2/groups/group-openid/messages", request.Path);
        Assert.Equal("QQBot test-token", request.Authorization);
    }

    [Fact]
    public async Task MarkdownRejected_FallsBackToPlainTextWithNewSeq()
    {
        var handler = new RecordingHandler((_, index) => index == 0
            ? Json(HttpStatusCode.BadRequest, """{"code":40034127,"message":"no markdown permission"}""")
            : Json(HttpStatusCode.OK, """{"id":"m2"}"""));
        using var http = new HttpClient(handler);
        await using var conn = new OfficialBotConnection(
            new OfficialBotConfig { AppId = "a", AppSecret = "b", MarkdownSupport = true }, http);

        await conn.SendTextAsync("group-openid", ChatType.Group, "hello", "msg-1");

        Assert.Equal(2, handler.Requests.Count);
        using var first = JsonDocument.Parse(handler.Requests[0].Body);
        using var second = JsonDocument.Parse(handler.Requests[1].Body);
        Assert.Equal(2, first.RootElement.GetProperty("msg_type").GetInt32());
        Assert.Equal(0, second.RootElement.GetProperty("msg_type").GetInt32());
        Assert.Equal("hello", second.RootElement.GetProperty("content").GetString());
        Assert.Equal(2, second.RootElement.GetProperty("msg_seq").GetInt32());
    }

    [Fact]
    public async Task MarkdownFallback_FormatsMarkdown_ButKeepsPlainOriginal()
    {
        var handler = new RecordingHandler((_, index) => index == 0
            ? Json(HttpStatusCode.BadRequest, """{"code":40034127,"message":"no markdown permission"}""")
            : Json(HttpStatusCode.OK, """{"id":"m2"}"""));
        using var http = new HttpClient(handler);
        await using var conn = new OfficialBotConnection(
            new OfficialBotConfig { AppId = "a", AppSecret = "b", MarkdownSupport = true }, http);

        await conn.SendTextAsync("group-openid", ChatType.Group,
            "【结论】A\n【原因】B", "msg-1");

        Assert.Equal(2, handler.Requests.Count);
        using var first = JsonDocument.Parse(handler.Requests[0].Body);
        using var second = JsonDocument.Parse(handler.Requests[1].Body);
        Assert.Equal("## 结论\nA\n## 原因\nB",
            first.RootElement.GetProperty("markdown").GetProperty("content").GetString());
        Assert.Equal("【结论】A\n【原因】B", second.RootElement.GetProperty("content").GetString());
    }

    [Fact]
    public async Task ProactiveSend_OmitsMsgIdAndSeq()
    {
        var handler = new RecordingHandler((_, _) => Json(HttpStatusCode.OK, """{"id":"m1"}"""));
        using var http = new HttpClient(handler);
        await using var conn = new OfficialBotConnection(
            new OfficialBotConfig { AppId = "a", AppSecret = "b", MarkdownSupport = false }, http);

        // 默认接口方法只能经接口调用；也验证 IBotConnection 的主动发送入口可用
        await ((IBotConnection)conn).SendProactiveAsync("group-openid", ChatType.Group, "主动消息");

        using var doc = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        Assert.Equal(0, doc.RootElement.GetProperty("msg_type").GetInt32());
        Assert.Equal("主动消息", doc.RootElement.GetProperty("content").GetString());
        Assert.False(doc.RootElement.TryGetProperty("msg_id", out _));
        Assert.False(doc.RootElement.TryGetProperty("msg_seq", out _));
    }

    [Fact]
    public async Task MoreThanFivePassiveReplies_DowngradeToProactive()
    {
        var handler = new RecordingHandler((_, _) => Json(HttpStatusCode.OK, """{"id":"m"}"""));
        using var http = new HttpClient(handler);
        await using var conn = new OfficialBotConnection(
            new OfficialBotConfig { AppId = "a", AppSecret = "b", MarkdownSupport = false }, http);

        for (var i = 0; i < 6; i++)
            await conn.SendTextAsync("group-openid", ChatType.Group, $"第 {i + 1} 条", "msg-1");

        Assert.Equal(6, handler.Requests.Count);
        using var sixth = JsonDocument.Parse(handler.Requests[5].Body);
        Assert.False(sixth.RootElement.TryGetProperty("msg_id", out _));
        Assert.False(sixth.RootElement.TryGetProperty("msg_seq", out _));
    }

    [Fact]
    public async Task ExpiredPassiveReply_FallsBackToProactive()
    {
        var handler = new RecordingHandler((_, index) => index == 0
            ? Json(HttpStatusCode.BadRequest, """{"code":40034128,"message":"reply window expired"}""")
            : Json(HttpStatusCode.OK, """{"id":"m2"}"""));
        using var http = new HttpClient(handler);
        await using var conn = new OfficialBotConnection(
            new OfficialBotConfig { AppId = "a", AppSecret = "b", MarkdownSupport = false }, http);

        await conn.SendTextAsync("group-openid", ChatType.Group, "迟到的回复", "msg-1");

        Assert.Equal(2, handler.Requests.Count);
        using var second = JsonDocument.Parse(handler.Requests[1].Body);
        Assert.False(second.RootElement.TryGetProperty("msg_id", out _));
        Assert.False(second.RootElement.TryGetProperty("msg_seq", out _));
        Assert.Equal("迟到的回复", second.RootElement.GetProperty("content").GetString());
    }

    [Fact]
    public async Task MergedSend_CombinesSectionsIntoSinglePassiveMessage()
    {
        var handler = new RecordingHandler((_, _) => Json(HttpStatusCode.OK, """{"id":"m1"}"""));
        using var http = new HttpClient(handler);
        await using var conn = new OfficialBotConnection(
            new OfficialBotConfig { AppId = "a", AppSecret = "b", MarkdownSupport = false }, http);

        var ok = await conn.TrySendForwardAsync("group-openid", ChatType.Group,
            ["【结论】A", "【原因】B"], "msg-1");

        Assert.True(ok);
        using var doc = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        Assert.Equal("【结论】A\n\n【原因】B", doc.RootElement.GetProperty("content").GetString());
        Assert.Equal("msg-1", doc.RootElement.GetProperty("msg_id").GetString());
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(string Path, string Body, string? Authorization)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            // 发送前会先走 token 端点；这里模拟成功，避免测试依赖真实 QQ 凭证。
            if (request.RequestUri?.AbsolutePath == "/app/getAppAccessToken")
                return Json(HttpStatusCode.OK, """{"access_token":"test-token","expires_in":"7200"}""");

            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.RequestUri?.AbsolutePath ?? "", body, request.Headers.Authorization?.ToString()));
            return respond(request, Requests.Count - 1);
        }
    }
}

public class QqMarkdownFormatterTests
{
    [Fact]
    public void BracketSections_BecomeHeadings()
    {
        var text = "【结论】原生崩溃\n【原因分析】模组冲突\n【解决方法】更新模组";
        var formatted = QqMarkdownFormatter.Format(text);
        Assert.Equal("## 结论\n原生崩溃\n## 原因分析\n模组冲突\n## 解决方法\n更新模组", formatted);
    }

    [Fact]
    public void InlineBrackets_BecomeBold()
    {
        Assert.Equal("正文**重要**内容", QqMarkdownFormatter.Format("正文【重要】内容"));
    }

    [Fact]
    public void BareHeading_IsConverted()
    {
        Assert.Equal("## 结论\n内容", QqMarkdownFormatter.Format("结论\n内容"));
    }

    [Fact]
    public void CodeFence_IsNotModified()
    {
        var text = "```log\n【ERROR】boom\n```";
        Assert.Equal(text, QqMarkdownFormatter.Format(text));
    }
}

internal sealed class FakeConnection(string name) : IBotConnection
{
    public string Name { get; } = name;
    public bool IsConnected { get; private set; }
    public bool ForwardSupported { get; set; }
    public List<(string ChatId, string Text, string? ReplyToMessageId)> Sent { get; } = [];

    public event EventHandler<MessageReceivedEventArgs>? MessageReceived;
    public event EventHandler<string>? ConnectionLog;

    public Task StartAsync(CancellationToken ct = default)
    {
        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        IsConnected = false;
        return Task.CompletedTask;
    }

    public Task SendTextAsync(string chatId, ChatType chatType, string text, string? replyToMessageId = null,
        CancellationToken ct = default)
    {
        Sent.Add((chatId, text, replyToMessageId));
        return Task.CompletedTask;
    }

    public Task<bool> TrySendForwardAsync(string chatId, ChatType chatType, IReadOnlyList<string> sections,
        string? replyToMessageId = null, CancellationToken ct = default)
    {
        if (!ForwardSupported) return Task.FromResult(false);
        Sent.Add((chatId, string.Join("\n\n", sections), replyToMessageId));
        return Task.FromResult(true);
    }

    public ValueTask DisposeAsync()
    {
        IsConnected = false;
        return ValueTask.CompletedTask;
    }

    public void Emit(BotMessage message) =>
        MessageReceived?.Invoke(this, new MessageReceivedEventArgs(message));

    /// <summary>测试里模拟连接日志（生产实现会调用）。</summary>
    public void Log(string line) => ConnectionLog?.Invoke(this, line);
}
