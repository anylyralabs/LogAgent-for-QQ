using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LogAgent.Shared.Configuration;
using LogAgent.Shared.Connections;
using LogAgent.Shared.Messaging;

namespace LogAgent.Connections;

/// <summary>
/// QQ 官方机器人适配器（开放平台 API v2）：
/// access_token → gateway → WS（op10 Hello / op2 Identify / op1 心跳 / op0 事件 / op6 Resume）。
/// 发送侧对齐 OpenClaw 的 QQ Bot 能力：
/// <list type="bullet">
/// <item>被动回复：携带 msg_id（5 分钟窗口、限 5 次），超限/过期自动降级主动消息；</item>
/// <item>Markdown：<c>msg_type=2</c>，平台无权限/内容不合法时自动回退纯文本；</item>
/// <item>合并消息：没有转发卡片，把多个分节合并成一条消息；</item>
/// <item>主动消息：不带 msg_id；群聊主动消息需群主开启「机器人主动在群聊内发言」。</item>
/// </list>
/// 规划 3.1B。凭证链路以官方文档为准（bot.q.qq.com）。
/// </summary>
public sealed class OfficialBotConnection : IBotConnection
{
    private static readonly Uri TokenEndpoint = new("https://bots.qq.com/app/getAppAccessToken");
    private static readonly Uri ProdBase = new("https://api.sgroup.qq.com");
    private static readonly Uri SandboxBase = new("https://sandbox.api.sgroup.qq.com");

    private readonly OfficialBotConfig _config;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _worker;

    private const int MaxPassiveReplies = 5;
    /// <summary>官方 Bot 单条消息的保守长度上限（超过则回退分段发送）。</summary>
    private const int MaxMergedMessageLength = 4500;

    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _tokenExpiresAt;
    private TimeSpan _tokenRefreshMargin = TimeSpan.FromMinutes(5);
    private string? _sessionId;
    private string? _selfOpenId;
    private long _lastSeq;
    private readonly ConcurrentDictionary<string, int> _replyCount = [];     // 每个 msg_id 回复序号（≤5）
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seenMessages = []; // 平台可能重复推送同一 msg_id

    public string Name => $"QQ官方Bot ({_config.AppId}{(_config.Sandbox ? " 沙箱" : "")})";
    public bool IsConnected => _ws is { State: WebSocketState.Open };

    public event EventHandler<MessageReceivedEventArgs>? MessageReceived;
    public event EventHandler<string>? ConnectionLog;

    public OfficialBotConnection(OfficialBotConfig config, HttpClient? http = null)
    {
        _config = config;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _ownsHttp = http is null;
    }

    private Uri ApiBase => _config.Sandbox ? SandboxBase : ProdBase;

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_config.AppId) || string.IsNullOrEmpty(_config.AppSecret))
            throw new InvalidOperationException("官方 Bot 未配置 AppId/AppSecret");

        await RefreshTokenAsync(ct);
        var gatewayUrl = await GetGatewayAsync(ct);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _worker = Task.Run(() => RunWsAsync(gatewayUrl, _cts.Token), _cts.Token);
        Log("网关连接已发起");
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        _cts?.Cancel();
        // 直接 Abort 打断 ReceiveAsync；WebSocket 关闭握手在网关异常时可能一直等，
        // 会让 systemd 触发 TimeoutStopSec 并错过“切段会话”。
        try { _ws?.Abort(); }
        catch (WebSocketException) { }
        catch (ObjectDisposedException) { }

        if (_worker is not null)
        {
            try { await _worker.WaitAsync(TimeSpan.FromSeconds(5), ct); }
            catch (OperationCanceledException) { }
            catch (TimeoutException) { Log("WS 接收循环未在 5s 内退出，继续停止"); }
        }
        Log("官方 Bot 已停止");
    }

    public async Task SendTextAsync(string chatId, ChatType chatType, string text, string? replyToMessageId = null, CancellationToken ct = default)
        => await SendTextCoreAsync(chatId, chatType, text, replyToMessageId, _config.MarkdownSupport, ct);

    /// <summary>
    /// 官方 Bot 没有合并转发卡片，改用「合并为一条消息」：
    /// 把多个分节拼成一条 Markdown/文本，避免一次回复消耗多次被动回复额度。
    /// 合并后仍超长时返回 false，由回复策略回退分段发送。
    /// </summary>
    public async Task<bool> TrySendForwardAsync(string chatId, ChatType chatType,
        IReadOnlyList<string> sections, string? replyToMessageId = null, CancellationToken ct = default)
    {
        if (sections.Count == 0) return false;
        var merged = string.Join("\n\n", sections);
        if (merged.Length == 0 || merged.Length > MaxMergedMessageLength) return false;
        return await SendTextCoreAsync(chatId, chatType, merged, replyToMessageId, _config.MarkdownSupport, ct);
    }

    /// <summary>
    /// 统一发送：优先被动回复（带 msg_id）；额度/时间窗口耗尽或主动发送时退化为无 msg_id 的主动消息。
    /// Markdown 被平台拒绝时自动回退纯文本，保证「能发出去」优先。
    /// </summary>
    private async Task<bool> SendTextCoreAsync(string chatId, ChatType chatType, string text,
        string? replyToMessageId, bool markdown, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(text)) return false;

        var path = chatType == ChatType.Group
            ? $"/v2/groups/{chatId}/messages"
            : $"/v2/users/{chatId}/messages";
        var uri = new Uri(ApiBase, path);

        var passive = !string.IsNullOrEmpty(replyToMessageId);
        var msgId = replyToMessageId;
        var seq = 0;
        void AllocatePassiveSeq()
        {
            if (!passive || msgId is null) return;
            seq = NextReplySeq(msgId);
            if (seq <= MaxPassiveReplies) return;
            Log($"[发送] msg_id={msgId} 被动回复次数超限（{MaxPassiveReplies} 次），降级主动消息");
            passive = false;
            msgId = null;
        }

        if (passive) AllocatePassiveSeq();
        else
        {
            Log(chatType == ChatType.Group
                ? "[发送] 主动群消息（需群主在 QQ 中开启「机器人主动在群聊内发言」）"
                : "[发送] 主动私聊消息（用户可在 QQ 中关闭主动消息）");
        }

        // 官方 Bot 的 Markdown 渲染：把【标题】/裸标题转成 ##，并去掉中文大括号。
        // 纯文本回退仍用原文，避免把 ## 当作普通文字发出去。
        var markdownText = markdown ? QqMarkdownFormatter.Format(text) : text;
        var useMarkdown = markdown;
        var result = await PostMessageAsync(uri, useMarkdown ? markdownText : text,
            msgId, passive ? seq : null, useMarkdown, ct);

        // Markdown 无权限/语法不被接受 → 纯文本重试（必须换 msg_seq，避免被平台去重）
        if (!result.Ok && useMarkdown && IsMarkdownError(result))
        {
            Log($"[发送] Markdown 被平台拒绝（{result.ErrorCode?.ToString() ?? "unknown"}），回退纯文本");
            useMarkdown = false;
            if (passive) AllocatePassiveSeq();
            result = await PostMessageAsync(uri, text, msgId, passive ? seq : null, false, ct);
        }

        // 被动回复窗口过期/次数超限 → 主动消息兜底（群聊需群主开启主动发言）
        if (!result.Ok && passive && result.ErrorCode == 40034128)
        {
            Log("[发送] 被动回复窗口已过期，尝试主动消息（可能被平台或用户设置拒绝）");
            passive = false;
            msgId = null;
            useMarkdown = markdown;
            result = await PostMessageAsync(uri, useMarkdown ? markdownText : text, null, null, useMarkdown, ct);
            if (!result.Ok && useMarkdown && IsMarkdownError(result))
                result = await PostMessageAsync(uri, text, null, null, false, ct);
        }

        if (result.Ok) return true;

        Log($"[发送] 失败 HTTP {result.Status}: {result.Body}{FriendlyHint(result.ErrorCode, chatType)}");
        return false;
    }

    private int NextReplySeq(string msgId)
    {
        if (_replyCount.Count > 2048) _replyCount.Clear(); // 防止长跑后无限增长
        return _replyCount.AddOrUpdate(msgId, 1, (_, n) => n + 1);
    }

    /// <summary>平台可能重复推送同一 msg_id（官方文档明确提示），这里做有界去重，避免同一问题回复两次。</summary>
    private bool MarkSeen(string messageId)
    {
        var now = DateTimeOffset.UtcNow;
        if (!_seenMessages.TryAdd(messageId, now)) return false;
        if (_seenMessages.Count <= 4096) return true;

        // 超过上限时先清掉 10 分钟前的记录；仍超限就整体清空（被动回复窗口只有 5 分钟）
        foreach (var (id, seenAt) in _seenMessages.ToArray())
        {
            if (now - seenAt > TimeSpan.FromMinutes(10))
                _seenMessages.TryRemove(id, out _);
        }
        if (_seenMessages.Count > 4096) _seenMessages.Clear();
        return true;
    }

    private async Task<SendResult> PostMessageAsync(Uri uri, string text, string? msgId, int? seq,
        bool markdown, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["msg_type"] = markdown ? 2 : 0,
        };
        if (markdown) body["markdown"] = new { content = text };
        else body["content"] = text;
        if (msgId is not null)
        {
            body["msg_id"] = msgId;
            body["msg_seq"] = seq ?? 1;
        }

        try
        {
            // 发送 API 必须带 Authorization: QQBot <access_token>；token 快过期时先刷新
            await RefreshTokenAsync(ct);
            using var req = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = JsonContent.Create(body),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("QQBot", _accessToken);
            req.Headers.TryAddWithoutValidation("X-Union-Appid", _config.AppId);

            using var resp = await _http.SendAsync(req, ct);
            var raw = await resp.Content.ReadAsStringAsync(ct);
            return resp.IsSuccessStatusCode
                ? new SendResult(true, (int)resp.StatusCode, raw, null)
                : new SendResult(false, (int)resp.StatusCode, raw, ParseErrorCode(raw));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 网络错误/超时：交给上层统一记录，避免异常冒到 Agent 消息泵
            return new SendResult(false, 0, ex.Message, null);
        }
    }

    private static bool IsMarkdownError(SendResult result) =>
        result.ErrorCode is 40034011 or 40034124 or 40034127
        || result.Body.Contains("markdown", StringComparison.OrdinalIgnoreCase);

    private static int? ParseErrorCode(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var name in new[] { "code", "err_code", "error_code" })
            {
                if (!doc.RootElement.TryGetProperty(name, out var el)) continue;
                if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var number)) return number;
                if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out number)) return number;
            }
        }
        catch (JsonException)
        {
            // 非 JSON 错误体：保留原文给日志
        }
        return null;
    }

    private static string FriendlyHint(int? code, ChatType chatType) => code switch
    {
        40034100 => "（主动消息触发频控，请降低频率或等待配额恢复）",
        40034105 => chatType == ChatType.Group
            ? "（没有主动消息权限：请群主在 QQ 群设置中开启「机器人主动在群聊内发言」）"
            : "（没有主动消息权限：用户可能关闭了机器人主动消息）",
        40034127 => "（没有 Markdown 权限：可在频道配置里关闭 Markdown 后重试）",
        40034011 or 40034124 => "（Markdown 内容不被平台接受，已尝试纯文本回退）",
        40034128 => chatType == ChatType.Group
            ? "（被动回复已过期或超过 5 次；降级主动消息需群主开启「机器人主动在群聊内发言」）"
            : "（被动回复已过期或超过 5 次）",
        40034101 or 40054003 => "（机器人不在该群，请先把机器人拉进群）",
        40054013 => "（用户拒收主动消息）",
        _ => "",
    };

    private readonly record struct SendResult(bool Ok, int Status, string Body, int? ErrorCode);

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        if (_ownsHttp) _http.Dispose();
    }

    private async Task RunWsAsync(string gatewayUrl, CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            var cleanExit = false;
            try
            {
                await ConnectOnceAsync(gatewayUrl, ct);
                cleanExit = true;
                backoff = TimeSpan.FromSeconds(1);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log($"WS 断开：{ex.Message}，{backoff.TotalSeconds}s 后重连");
                await Task.Delay(backoff, ct);
                backoff = backoff.TotalSeconds < 60
                    ? TimeSpan.FromMilliseconds(backoff.TotalMilliseconds * 2)
                    : TimeSpan.FromSeconds(60);
            }

            // 正常断开也稍等，避免网关连续 op9 时形成无间隔热循环。
            if (cleanExit) await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    private async Task ConnectOnceAsync(string gatewayUrl, CancellationToken ct)
    {
        // 重连时 token 可能已过期；必须先刷新，否则网关会返回 op9 Invalid Session。
        await RefreshTokenAsync(ct);

        _ws?.Dispose();
        _ws = new ClientWebSocket();
        _ws.Options.SetRequestHeader("Authorization", $"QQBot {_accessToken}");
        _ws.Options.SetRequestHeader("X-Union-Appid", _config.AppId);
        await _ws.ConnectAsync(new Uri(gatewayUrl), ct);
        Log("WS 已连接网关");

        // 读取 Hello（op 10）
        var hello = await ReceiveJsonAsync(ct);
        if (hello is null || hello.Value.TryGetProperty("op", out var op) && op.GetInt32() != 10)
            throw new InvalidOperationException("网关握手失败：未收到 op10 Hello");
        var heartbeatInterval = ReadInt(hello.Value.GetProperty("d"), "heartbeat_interval", 40000);

        // Identify（op 2）或 Resume（op 6）
        if (_sessionId is null)
        {
            await SendJsonAsync(new
            {
                op = 2,
                d = new
                {
                    token = $"QQBot {_accessToken}",
                    intents = 1 << 25, // GROUP_AND_C2C_EVENT：群@消息 + 群全量消息（如已开启）+ 单聊
                    shard = new[] { 0, 1 },
                    properties = new Dictionary<string, string>
                    {
                        ["$os"] = "windows",
                        ["$browser"] = "LogAgent",
                        ["$device"] = "LogAgent",
                    },
                },
            }, ct);
        }
        else
        {
            await SendJsonAsync(new { op = 6, d = new { token = $"QQBot {_accessToken}", session_id = _sessionId, seq = _lastSeq } }, ct);
        }

        // 心跳任务
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeat = Task.Run(async () =>
        {
            while (!heartbeatCts.Token.IsCancellationRequested)
            {
                await Task.Delay(heartbeatInterval, heartbeatCts.Token);
                await SendJsonAsync(new { op = 1, d = (long?)_lastSeq }, heartbeatCts.Token);
            }
        }, heartbeatCts.Token);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var payload = await ReceiveJsonAsync(ct);
                if (payload is null) break;
                HandlePayload(payload.Value);
            }
        }
        finally
        {
            heartbeatCts.Cancel();
            try { await heartbeat; } catch (OperationCanceledException) { }
        }
    }

    private void HandlePayload(JsonElement payload)
    {
        if (!payload.TryGetProperty("op", out var opEl)) return;
        switch (opEl.GetInt32())
        {
            case 0: // Dispatch
                if (payload.TryGetProperty("s", out var s)) _lastSeq = s.GetInt64();
                if (!payload.TryGetProperty("t", out var t)) break;
                var type = t.GetString();
                if (type == "READY")
                {
                    var ready = payload.GetProperty("d");
                    _sessionId = ready.GetProperty("session_id").GetString();
                    // 记录机器人自己的 openid：群全量消息里用它判断是否 @ 了机器人
                    if (ready.TryGetProperty("user", out var self) &&
                        self.TryGetProperty("id", out var selfId))
                        _selfOpenId = selfId.GetString();
                    Log($"鉴权成功，session={_sessionId?[..Math.Min(8, _sessionId.Length)]}…" +
                        (_selfOpenId is null ? "" : $"，self={_selfOpenId[..Math.Min(8, _selfOpenId.Length)]}…"));
                }
                else if (type is "GROUP_AT_MESSAGE_CREATE" or "GROUP_MESSAGE_CREATE" or "C2C_MESSAGE_CREATE")
                {
                    var isGroup = type is not "C2C_MESSAGE_CREATE";
                    // GROUP_MESSAGE_CREATE 是群全量消息，只有用户 @ 机器人才会走 GROUP_AT_MESSAGE_CREATE；
                    // 全量消息作为上下文记录，不默认触发回复（除非命中关键词规则）。
                    var mentioned = type is "GROUP_AT_MESSAGE_CREATE";
                    var msg = ParseEvent(payload, isGroup, mentioned);
                    // 按「事件类型 + 消息 ID」去重：同一条消息若同时以全量/AT 两种事件到达，两者都保留；
                    // 同一事件的重复推送才丢弃（官方文档明确提示可能重复）。
                    if (msg is not null && MarkSeen($"{type}:{msg.MessageId}"))
                    {
                        // 全量群消息可能很频繁，只在 @ / 单聊时打印收信日志
                        if (type is not "GROUP_MESSAGE_CREATE")
                            Log($"[收到] {msg.ChatType}:{msg.SenderId}: {msg.Text[..Math.Min(60, msg.Text.Length)]}");
                        MessageReceived?.Invoke(this, new MessageReceivedEventArgs(msg));
                    }
                }
                else if (type == "RESUMED")
                {
                    Log("Resume 补发完成");
                }
                break;
            case 11: // Heartbeat ACK
                break;
            case 7: // Reconnect
                Log("服务端要求重连");
                _ws?.Abort();
                break;
            case 9: // Invalid Session
                _sessionId = null;
                _lastSeq = 0;
                Log("会话无效，将重新 Identify");
                _ws?.Abort(); // 打断接收循环，交给 RunWsAsync 退避重连
                break;
        }
    }

    /// <summary>把官方 Bot 的 MESSAGE_CREATE 事件解析成统一消息（public 便于协议层单测）。</summary>
    public BotMessage? ParseEvent(JsonElement payload, bool isGroup, bool mentionedMe = true)
    {
        var d = payload.GetProperty("d");
        var content = d.TryGetProperty("content", out var c) ? c.GetString()?.Trim() : null;
        var (attachments, images) = ParseAttachmentBundle(d);
        var quote = ParseQuote(d);

        // 引用消息的正文在 msg_elements 里；即使当前消息只有引用/附件，也不能丢事件。
        var text = ComposeText(content, quote);
        if (string.IsNullOrEmpty(text) && attachments.Count == 0 && images.Count == 0) return null;

        // 引用里的文件同样交给富化环节下载；图片走多模态，不当作二进制附件。
        if (quote is not null)
        {
            attachments.AddRange(quote.Attachments);
            images.AddRange(quote.Images);
        }

        // 群全量模式下平台可能只发 GROUP_MESSAGE_CREATE；判断正文里的 <@...> 是否是机器人。
        // 注意只检查当前消息正文，避免引用里提到机器人就误触发。
        if (isGroup && !mentionedMe && MentionsSelf(d, content, _selfOpenId))
            mentionedMe = true;

        var author = d.TryGetProperty("author", out var a) ? a : (JsonElement?)null;
        // 防止机器人自己（或其它机器人）的消息进入循环；全量群消息模式下尤其重要。
        if (author?.TryGetProperty("bot", out var botEl) == true && botEl.ValueKind == JsonValueKind.True)
            return null;
        var chatId = isGroup
            ? d.TryGetProperty("group_openid", out var g) ? g.GetString() : null
            : (d.TryGetProperty("openid", out var o) ? o.GetString() : null)
              ?? (author?.TryGetProperty("user_openid", out var userOpenId) == true ? userOpenId.GetString() : null)
              ?? (author?.TryGetProperty("id", out var authorId) == true ? authorId.GetString() : null);
        if (chatId is null) return null;
        // 被动回复的 msg_id 来自事件体 d.id；兼容外层 id（部分网关会把消息 id 放在顶层）
        var messageId = (d.TryGetProperty("id", out var innerId) ? innerId.GetString() : null)
            ?? (payload.TryGetProperty("id", out var outerId) ? outerId.GetString() : null);
        var senderName = author?.TryGetProperty("username", out var un) == true ? un.GetString() ?? "" : "";
        var timestamp = d.TryGetProperty("timestamp", out var tsEl) && tsEl.ValueKind == JsonValueKind.String
                        && DateTimeOffset.TryParse(tsEl.GetString(), out var parsed)
            ? parsed
            : DateTimeOffset.Now;

        return new BotMessage
        {
            MessageId = messageId ?? Guid.NewGuid().ToString("N"),
            ChatId = chatId,
            ChatType = isGroup ? ChatType.Group : ChatType.Private,
            SenderId = author?.TryGetProperty("member_openid", out var mo) == true ? mo.GetString() ?? "" :
                       author?.TryGetProperty("user_openid", out var uo) == true ? uo.GetString() ?? "" :
                       author?.TryGetProperty("id", out var uid) == true ? uid.GetString() ?? "" : "",
            SenderName = senderName,
            Text = text,
            MentionedMe = mentionedMe, // GROUP_AT_MESSAGE_CREATE 天然是 @ 机器人；全量群消息为 false
            Timestamp = timestamp,
            RawJson = payload.GetRawText(),
            Attachments = attachments.Count > 0 ? attachments : null,
            ImageUrls = images.Count > 0 ? images.Distinct(StringComparer.OrdinalIgnoreCase).ToList() : null,
        };
    }

    /// <summary>官方 Bot 引用消息的解析结果：引用正文 + 引用者昵称 + 被引附件/图片。</summary>
    private sealed record QuoteInfo(
        string Text,
        string Sender,
        List<MessageAttachment> Attachments,
        List<string> Images);

    /// <summary>
    /// 解析 message_type=103 的引用消息。被引用内容在 d.msg_elements[] 中（content/attachments，
    /// 可能还有嵌套 msg_elements）；message_scene.ext 里的 ref_msg_idx 只是索引，不需要再调接口。
    /// </summary>
    private static QuoteInfo? ParseQuote(JsonElement d)
    {
        if (!d.TryGetProperty("msg_elements", out var elements) || elements.ValueKind != JsonValueKind.Array)
            return null;

        var text = "";
        var sender = "";
        var attachments = new List<MessageAttachment>();
        var images = new List<string>();
        foreach (var el in elements.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            if (text.Length == 0) text = ElementText(el);
            if (sender.Length == 0) sender = ElementSender(el);
            var (files, pics) = ParseAttachmentBundle(el);
            attachments.AddRange(files);
            images.AddRange(pics);
        }

        return text.Length > 0 || attachments.Count > 0 || images.Count > 0
            ? new QuoteInfo(text, sender, attachments, images)
            : null;
    }

    /// <summary>引用元素正文；content 为空时递归找嵌套引用，卡片则退化为可读标题。</summary>
    private static string ElementText(JsonElement el, int depth = 0)
    {
        if (depth > 3) return "";
        if (el.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
        {
            var text = content.GetString()?.Trim();
            if (!string.IsNullOrEmpty(text)) return text;
        }

        if (el.TryGetProperty("ark_data", out var ark) && ark.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "prompt", "title" })
            {
                if (ark.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(v.GetString()))
                    return v.GetString()!.Trim();
            }
        }

        if (el.TryGetProperty("msg_elements", out var nested) && nested.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in nested.EnumerateArray())
            {
                var text = ElementText(child, depth + 1);
                if (text.Length > 0) return text;
            }
        }
        return "";
    }

    /// <summary>引用元素作者昵称；没有昵称时退化为 openid，至少让模型知道是谁说的。</summary>
    private static string ElementSender(JsonElement el)
    {
        if (!el.TryGetProperty("author", out var author) || author.ValueKind != JsonValueKind.Object)
            return "";
        foreach (var name in new[] { "username", "member_openid", "user_openid", "id" })
        {
            if (author.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(v.GetString()))
                return v.GetString()!.Trim();
        }
        return "";
    }

    /// <summary>把引用正文与当前消息拼成模型可读的上下文，避免把引用内容误当成用户本次指令。</summary>
    private static string ComposeText(string? current, QuoteInfo? quote)
    {
        if (quote is null) return current ?? "";

        var quoteText = quote.Text.Trim();
        if (quoteText.Length > 500) quoteText = quoteText[..500] + "…";
        if (quoteText.Length == 0)
            quoteText = quote.Images.Count > 0 && quote.Attachments.Count > 0 ? "（含图片和文件）"
                : quote.Images.Count > 0 ? "（含图片）"
                : quote.Attachments.Count > 0 ? "（含文件）"
                : "（内容为空）";

        var sender = string.IsNullOrWhiteSpace(quote.Sender) ? "" : quote.Sender + "：";
        var quoted = $"[引用消息] {sender}{quoteText}";
        return string.IsNullOrWhiteSpace(current) ? quoted : $"{quoted}\n[当前消息] {current.Trim()}";
    }

    private static readonly Regex MentionPattern = new(
        @"<@!?([A-Za-z0-9_\-]+)>", RegexOptions.Compiled);

    /// <summary>
    /// 判断群消息正文是否 @ 了机器人。官方约定 <c>mentions</c> 不包含机器人自身，
    /// 所以「正文里的 &lt;@id&gt;」减去「mentions 里的其他用户」后还有剩余，就是 @ 了机器人。
    /// READY 里的 self openid 能直接匹配时优先使用（群成员 openid 可能与 self 不同）。
    /// </summary>
    private static bool MentionsSelf(JsonElement d, string? content, string? selfOpenId)
    {
        // 全量群消息的 mentions 里可能带 is_you=true（平台权威标记），优先采用。
        if (d.TryGetProperty("mentions", out var authoritative) &&
            authoritative.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in authoritative.EnumerateArray())
            {
                if (m.TryGetProperty("is_you", out var you) && you.ValueKind == JsonValueKind.True)
                    return true;
            }
        }

        if (string.IsNullOrEmpty(content)) return false;
        if (!string.IsNullOrEmpty(selfOpenId) &&
            (content.Contains($"<@{selfOpenId}>", StringComparison.Ordinal) ||
             content.Contains($"<@!{selfOpenId}>", StringComparison.Ordinal)))
            return true;

        var mentioned = MentionPattern.Matches(content)
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (mentioned.Count == 0) return false;

        if (d.TryGetProperty("mentions", out var mentions) && mentions.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in mentions.EnumerateArray())
            {
                foreach (var name in new[] { "id", "member_openid", "user_openid" })
                {
                    if (m.TryGetProperty(name, out var idEl) && idEl.ValueKind == JsonValueKind.String)
                        mentioned.Remove(idEl.GetString() ?? "");
                }
            }
        }

        // 剩下的一定是没有出现在 mentions 里的被 @ 对象；官方语境下就是机器人
        return mentioned.Count > 0;
    }

    /// <summary>解析官方 Bot 消息附件（attachments[]：filename/url/size/content_type；url 有效期约 10 分钟需尽快下载）。</summary>
    public static List<MessageAttachment> ParseAttachments(JsonElement d) =>
        ParseAttachmentItems(d).Select(i => i.Attachment).ToList();

    /// <summary>附件原始信息（含 content_type，用于区分图片与文件）。</summary>
    private static List<(MessageAttachment Attachment, string? ContentType)> ParseAttachmentItems(JsonElement d)
    {
        var result = new List<(MessageAttachment, string?)>();
        if (!d.TryGetProperty("attachments", out var attachments) || attachments.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var att in attachments.EnumerateArray())
        {
            if (!att.TryGetProperty("url", out var urlEl) || urlEl.ValueKind != JsonValueKind.String) continue;
            var url = urlEl.GetString();
            if (string.IsNullOrEmpty(url)) continue;
            var contentType = att.TryGetProperty("content_type", out var ctv) && ctv.ValueKind == JsonValueKind.String
                ? ctv.GetString()
                : null;
            var name = att.TryGetProperty("filename", out var fn) && fn.ValueKind == JsonValueKind.String
                ? fn.GetString()
                : null;
            if (string.IsNullOrEmpty(name))
                name = $"附件-{contentType ?? "unknown"}";
            var size = att.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number ? sz.GetInt64() : 0;
            result.Add((new MessageAttachment(name!, url!, size), contentType));
        }
        return result;
    }

    /// <summary>
    /// 把附件拆成「文件」与「图片 URL」：图片交给多模态读图，
    /// 文件交给消息富化下载/读取，避免图片被当成二进制附件后直接报“不是可读文本”。
    /// </summary>
    private static (List<MessageAttachment> Files, List<string> Images) ParseAttachmentBundle(JsonElement d)
    {
        var files = new List<MessageAttachment>();
        var images = new List<string>();
        foreach (var (attachment, contentType) in ParseAttachmentItems(d))
        {
            if (contentType is not null &&
                (contentType.Equals("image", StringComparison.OrdinalIgnoreCase) ||
                 contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)))
                images.Add(attachment.Url);
            else
                files.Add(attachment);
        }
        return (files, images);
    }

    private async Task<JsonElement?> ReceiveJsonAsync(CancellationToken ct)
    {
        var buffer = new byte[32 * 1024];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await _ws!.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return (JsonElement?)JsonDocument.Parse(Encoding.UTF8.GetString(ms.ToArray())).RootElement.Clone();
    }

    private Task SendJsonAsync(object payload, CancellationToken ct)
        => _ws!.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)),
            WebSocketMessageType.Text, true, ct);

    private bool HasValidToken =>
        _accessToken is not null && DateTimeOffset.UtcNow < _tokenExpiresAt - _tokenRefreshMargin;

    private async Task RefreshTokenAsync(CancellationToken ct)
    {
        if (HasValidToken) return;

        // 令牌接口偶尔只返回 180s；固定“提前 5 分钟刷新”会导致每次调用都刷新，
        // 甚至互相顶掉会话。这里按实际 TTL 动态计算提前量，并串行化刷新。
        await _tokenGate.WaitAsync(ct);
        try
        {
            if (HasValidToken) return;
            using var resp = await _http.PostAsJsonAsync(TokenEndpoint,
                new { appId = _config.AppId, clientSecret = _config.AppSecret }, ct);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            _accessToken = doc.RootElement.GetProperty("access_token").GetString()
                ?? throw new InvalidOperationException("获取 access_token 失败");

            // 注意：官方 expires_in 返回的是字符串（"7200"），必须兼容读取
            var expiresIn = Math.Max(30, ReadInt(doc.RootElement, "expires_in", 7200));
            _tokenRefreshMargin = TimeSpan.FromSeconds(Math.Clamp(expiresIn / 3, 15, 300));
            _tokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
            Log($"access_token 已刷新（{expiresIn}s，提前 {_tokenRefreshMargin.TotalSeconds:F0}s 刷新）");
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    /// <summary>兼容字符串/数字的 JSON 整数字段读取（官方 API 混用两种类型）。</summary>
    private static int ReadInt(JsonElement el, string name, int fallback)
    {
        if (!el.TryGetProperty(name, out var v)) return fallback;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetInt32(),
            JsonValueKind.String => int.TryParse(v.GetString(), out var n) ? n : fallback,
            _ => fallback,
        };
    }

    private async Task<string> GetGatewayAsync(CancellationToken ct)
    {
        await RefreshTokenAsync(ct);
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(ApiBase, "/gateway"));
        req.Headers.Authorization = new("QQBot", _accessToken);
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("url").GetString()!;
    }

    private void Log(string message) => ConnectionLog?.Invoke(this, $"[官方Bot] {message}");
}
