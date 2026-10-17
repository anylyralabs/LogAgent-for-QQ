using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using LogAgent.Shared.Configuration;
using LogAgent.Shared.Connections;
using LogAgent.Shared.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace LogAgent.Connections;

/// <summary>
/// OneBot v11 反向 WebSocket 适配器：我方为 WS 服务端（Kestrel），NapCat 主动连入。
/// 双向通道：接收事件 JSON + 下发 API action（带 echo 应答匹配）。规划 3.1A。
/// </summary>
public sealed class OneBot11Connection : IBotConnection
{
    private readonly OneBot11Config _config;
    private WebApplication? _app;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private volatile WebSocket? _socket;
    private long _selfId;
    private string _botNickname = "";
    private int _echoSeq;

    public string Name => $"OneBot11 反向WS :{_config.Host}:{_config.Port}{_config.Path}";
    public bool IsConnected => _socket is { State: WebSocketState.Open };

    public event EventHandler<MessageReceivedEventArgs>? MessageReceived;
    public event EventHandler<string>? ConnectionLog;

    public OneBot11Connection(OneBot11Config config) => _config = config;

    public Task StartAsync(CancellationToken ct = default)
    {
        if (_app is not null) return Task.CompletedTask;

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = System.IO.Path.GetTempPath(),
        });
        builder.WebHost.ConfigureKestrel(o =>
            o.Listen(IPAddress.Parse(_config.Host), _config.Port));
        builder.Logging.ClearProviders();
        var app = builder.Build();

        app.UseWebSockets();
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path != _config.Path) { await next(); return; }
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 404; return; }
            if (!Authorize(ctx.Request)) { ctx.Response.StatusCode = 401; Log("鉴权失败，拒绝连接"); return; }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            Log("NapCat 已连入");
            _socket = ws;
            await ReceiveLoopAsync(ws, ct);
        });

        _app = app;
        Log($"WS 服务端启动于 {Name}");
        return app.StartAsync(ct);
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_socket is { } ws)
        {
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "shutdown", ct); }
            catch (WebSocketException) { }
        }
        if (_app is { } app)
        {
            await app.StopAsync(ct);
            await app.DisposeAsync();
            _app = null;
            Log("WS 服务端已停止");
        }
    }

    public async Task SendTextAsync(string chatId, ChatType chatType, string text, string? replyToMessageId = null, CancellationToken ct = default)
    {
        var socket = _socket ?? throw new InvalidOperationException("NapCat 未连接（WS 客户端不存在）");
        var isGroup = chatType == ChatType.Group;
        if (!long.TryParse(chatId, out var id))
            throw new ArgumentException($"OneBot11 的会话 ID 应为数字，收到：{chatId}");
        var echo = $"echo-{Interlocked.Increment(ref _echoSeq)}";
        var payload = new
        {
            action = isGroup ? "send_group_msg" : "send_private_msg",
            @params = new
            {
                group_id = isGroup ? id : (long?)null,
                user_id = isGroup ? (long?)null : id,
                message = text,
            },
            echo,
        };
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[echo] = tcs;
        try
        {
            await socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)),
                WebSocketMessageType.Text, true, ct);
            var done = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(15), ct));
            if (done != tcs.Task) Log($"API 调用 {echo} 超时（消息可能仍已送达）");
            // 注意：OneBot11 响应形如 {"status":"ok","retcode":0,...}，retcode 是顶层字段（status 是字符串）
            else if (tcs.Task.Result.TryGetProperty("retcode", out var rc) && rc.ValueKind == JsonValueKind.Number && rc.GetInt32() != 0)
                Log($"API 调用失败：{tcs.Task.Result.GetRawText()}");
        }
        finally
        {
            _pending.TryRemove(echo, out _);
        }
    }

    /// <summary>
    /// 合并转发发送（NapCat：send_group_forward_msg / send_private_forward_msg）。
    /// 一个转发卡片承载多个分节（原因/方法/建议等），点开逐节阅读——长报告不再刷屏。
    /// 失败返回 false，由调用方回退普通发送。
    /// </summary>
    public async Task<bool> SendForwardAsync(string chatId, ChatType chatType, IReadOnlyList<string> sections, CancellationToken ct = default)
    {
        if (sections.Count == 0) return false;
        if (!long.TryParse(chatId, out var id)) return false;

        // 发送者身份：优先 lifecycle 缓存的 self_id；否则现场取一次登录信息（顺带拿真实昵称）
        var selfId = _selfId;
        var nickname = "小炎";
        if (selfId == 0 || _botNickname.Length == 0)
        {
            var (loginResp, _) = await CallApiAsync("get_login_info", new { }, ct);
            if (loginResp is { } l && l.TryGetProperty("data", out var d))
            {
                if (selfId == 0 && d.TryGetProperty("user_id", out var u))
                    selfId = u.GetInt64();
                if (_botNickname.Length == 0 && d.TryGetProperty("nickname", out var nk))
                    _botNickname = LooseString(nk);
            }
        }
        if (selfId == 0) return false;
        if (_botNickname.Length > 0) nickname = _botNickname;

        var nodes = sections.Select(s => (object)new
        {
            type = "node",
            data = new
            {
                user_id = selfId,
                nickname,
                message_seq = 0,
                content = new object[] { new { type = "text", data = new { text = s } } },
            },
        }).ToList();

        var payload = chatType == ChatType.Group
            ? (object)new { group_id = id, message_seq = 0, messages = nodes }
            : new { user_id = id, message_seq = 0, messages = nodes };
        var action = chatType == ChatType.Group ? "send_group_forward_msg" : "send_private_forward_msg";

        var (resp, error) = await CallApiAsync(action, payload, ct);
        var ok = resp is not null;
        Log(ok
            ? $"[发送] 合并转发 {sections.Count} 段 → {chatType}:{chatId}"
            : $"[发送] 合并转发失败（{action}：{error ?? "无响应"}），回退普通发送");
        return ok;
    }

    public ValueTask DisposeAsync() => new(StopAsync().ContinueWith(_ => { }));

    private bool Authorize(HttpRequest request)
    {
        if (string.IsNullOrEmpty(_config.Token)) return true;
        var header = request.Headers.Authorization.ToString();
        if (header == $"Bearer {_config.Token}" || header == $"token {_config.Token}") return true;
        if (request.Query.TryGetValue("access_token", out var q) && q == _config.Token) return true;
        return false;
    }

    private async Task ReceiveLoopAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        Log("NapCat 连接已关闭，等待重连");
                        if (ReferenceEquals(_socket, ws)) _socket = null;
                        return;
                    }
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                var json = Encoding.UTF8.GetString(ms.ToArray());
                if (json.Length > 0) HandlePayload(json);
            }
        }
        catch (WebSocketException ex)
        {
            Log($"WS 异常断开：{ex.Message}");
            if (ReferenceEquals(_socket, ws)) _socket = null;
        }
    }

    private void HandlePayload(string json)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(json).RootElement; }
        catch (JsonException) { return; }

        // API 应答（echo 匹配）
        if (root.TryGetProperty("echo", out var echoEl) && echoEl.ValueKind == JsonValueKind.String)
        {
            if (_pending.TryRemove(echoEl.GetString()!, out var tcs))
                tcs.TrySetResult(root.Clone());
            return;
        }

        if (!root.TryGetProperty("post_type", out var postType)) return;
        switch (postType.GetString())
        {
            case "meta_event":
                if (root.TryGetProperty("meta_event_type", out var met) && met.GetString() == "lifecycle")
                {
                    if (root.TryGetProperty("self_id", out var self)) _selfId = self.GetInt64();
                    Log($"NapCat lifecycle 就绪（self_id={_selfId}）");
                }
                break;

            case "message" when root.TryGetProperty("message_type", out var mtype):
                _ = HandleMessageAsync(root.Clone(), mtype.GetString() == "group" ? ChatType.Group : ChatType.Private);
                break;
        }
    }

    private async Task HandleMessageAsync(JsonElement root, ChatType chatType)
    {
        try
        {
            var message = await ParseMessageAsync(root, chatType);
            if (message is not null)
            {
                var tail = message.Attachments is { Count: > 0 } ? $"（附件 {message.Attachments.Count} 个）" : "";
                Log($"[收到] {message.ChatType}:{message.ChatId} {message.SenderName}: {message.Text[..Math.Min(60, message.Text.Length)]}{tail}");
                MessageReceived?.Invoke(this, new MessageReceivedEventArgs(message));
            }
            else Log("[警告] 消息解析返回 null");
        }
        catch (Exception ex)
        {
            Log($"[错误] 消息处理异常：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task<BotMessage?> ParseMessageAsync(JsonElement root, ChatType chatType)
    {
        var chatId = chatType == ChatType.Group
            ? root.TryGetProperty("group_id", out var g) ? g.GetInt64().ToString() : null
            : root.TryGetProperty("user_id", out var u) ? u.GetInt64().ToString() : null;
        if (chatId is null) return null;

        var messageId = root.TryGetProperty("message_id", out var mid) ? mid.ToString() : Guid.NewGuid().ToString("N");
        var mentioned = false;
        string text;
        if (root.TryGetProperty("message", out var msgEl))
        {
            if (msgEl.ValueKind == JsonValueKind.Array)
            {
                text = ExtractPlainText(root.GetRawText());
                foreach (var seg in msgEl.EnumerateArray())
                {
                    if (seg.TryGetProperty("type", out var t) && t.GetString() == "at"
                        && seg.TryGetProperty("data", out var d) && d.TryGetProperty("qq", out var qq)
                        && qq.ToString() == _selfId.ToString())
                        mentioned = true;
                }
            }
            else
            {
                text = msgEl.GetString() ?? "";
                mentioned = text.Contains($"[CQ:at,qq={_selfId}]");
            }
        }
        else text = root.TryGetProperty("raw_message", out var raw) ? raw.GetString() ?? "" : "";

        string senderId = "", senderName = "";
        if (root.TryGetProperty("sender", out var sender) && sender.ValueKind == JsonValueKind.Object)
        {
            senderId = sender.TryGetProperty("user_id", out var su) ? LooseString(su) : "";
            var card = sender.TryGetProperty("card", out var c) ? LooseString(c) : "";
            senderName = card != ""
                ? card
                : sender.TryGetProperty("nickname", out var nick) ? LooseString(nick) : "";
        }
        if (senderId == "" && root.TryGetProperty("user_id", out var uu)) senderId = uu.ToString();

        var attachments = await ResolveAttachmentsAsync(root, chatType, chatId);
        var imageUrls = ExtractImageUrls(root.GetRawText());

        // reply 引用：取被引原消息文本并入；被引消息含文件/图片时一并解析
        if (await ResolveReplyQuoteAsync(root.GetRawText(), chatType, chatId) is { } quote)
        {
            if (quote.Text is not null)
                text = text.Length == 0 ? quote.Text : text + "\n\n" + quote.Text;
            attachments.AddRange(quote.Attachments);
            imageUrls.AddRange(quote.Images);
        }

        return new BotMessage
        {
            MessageId = messageId,
            ChatId = chatId,
            ChatType = chatType,
            SenderId = senderId,
            SenderName = senderName,
            Text = text,
            MentionedMe = mentioned,
            Timestamp = DateTimeOffset.Now,
            RawJson = root.GetRawText(),
            Attachments = attachments.Count > 0 ? attachments : null,
            ImageUrls = imageUrls.Count > 0 ? imageUrls : null,
        };
    }

    /// <summary>解析消息中的文件段并解析出下载地址（优先段内 url；否则调 NapCat API；失败时附原因）。</summary>
    private async Task<List<MessageAttachment>> ResolveAttachmentsAsync(JsonElement root, ChatType chatType, string chatId)
    {
        var result = new List<MessageAttachment>();
        foreach (var segment in ExtractFileSegments(root.GetRawText()))
        {
            var url = segment.DirectUrl;
            string? error = null;
            if (string.IsNullOrEmpty(url))
            {
                JsonElement? resp;
                if (chatType == ChatType.Group)
                {
                    (resp, error) = await CallApiAsync("get_group_file_url", new
                    {
                        group_id = long.TryParse(chatId, out var gid) ? gid : 0,
                        file_id = segment.FileId,
                        busid = segment.Busid,
                    });
                }
                else
                {
                    (resp, error) = await CallApiAsync("get_private_file_url", new
                    {
                        user_id = long.TryParse(chatId, out var uid) ? uid : 0,
                        file_id = segment.FileId,
                    });
                }
                if (resp is { } r && r.TryGetProperty("data", out var data)
                    && data.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String)
                    url = urlEl.GetString();
            }
            if (string.IsNullOrEmpty(url))
            {
                // 下载地址拿不到：不再静默跳过——把原因附到消息里，让模型能如实告知用户
                Log($"[附件] {segment.Name} 无法解析下载地址（{error ?? "接口未返回地址"}）");
                result.Add(new MessageAttachment(segment.Name, "", segment.Size)
                {
                    UnavailableReason = SummarizeFileApiError(error),
                });
                continue;
            }
            result.Add(new MessageAttachment(segment.Name, url, segment.Size));
        }
        return result;
    }

    /// <summary>把文件接口失败原因提炼为给模型看的短句。</summary>
    public static string SummarizeFileApiError(string? error)
    {
        if (string.IsNullOrEmpty(error)) return "文件接口未返回下载地址";
        if (error.Contains("packetBackend", StringComparison.OrdinalIgnoreCase))
            return "NapCat 的 packetBackend 不可用（NapCat 版本与 QQ 版本不匹配时会出现），文件下载地址无法获取";
        return error.Length > 120 ? error[..120] + "…" : error;
    }

    /// <summary>提取 reply 段引用的消息 id（无则 null）。</summary>
    public static string? ExtractReplyMessageId(string rawJson)
    {
        using var doc = JsonDocument.Parse(rawJson);
        if (!doc.RootElement.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var seg in message.EnumerateArray())
        {
            if (!seg.TryGetProperty("type", out var type) || type.GetString() != "reply") continue;
            if (seg.TryGetProperty("data", out var data) && data.TryGetProperty("id", out var id))
                return LooseString(id);
        }
        return null;
    }

    /// <summary>reply 引用解析结果：文本块（可为 null）+ 被引消息携带的文件附件 + 图片 URL。</summary>
    private sealed record ReplyQuote(string? Text, List<MessageAttachment> Attachments, List<string> Images);

    /// <summary>解析 reply 引用的原消息（NapCat get_msg）：原文文本 + 被引消息中的文件/图片；失败返回 null。</summary>
    private async Task<ReplyQuote?> ResolveReplyQuoteAsync(string rawJson, ChatType chatType, string chatId)
    {
        var replyId = ExtractReplyMessageId(rawJson);
        if (string.IsNullOrEmpty(replyId)) return null;
        try
        {
            object messageId = long.TryParse(replyId, out var mid) ? mid : replyId;
            var (resp, _) = await CallApiAsync("get_msg", new { message_id = messageId });
            if (resp is not { } r || !r.TryGetProperty("data", out var data))
                return new ReplyQuote("【引用了某条消息（原文获取失败）】", [], []);

            // 被引消息若含文件/图片 → 同样解析（引用文件/图片消息时，内容也能读到）
            var quoteAttachments = await ResolveAttachmentsAsync(data, chatType, chatId);
            var quoteImages = ExtractImageUrls(data.GetRawText());

            var senderName = "";
            if (data.TryGetProperty("sender", out var sender) && sender.ValueKind == JsonValueKind.Object)
            {
                var card = sender.TryGetProperty("card", out var c) ? LooseString(c) : "";
                senderName = card.Length > 0
                    ? card
                    : sender.TryGetProperty("nickname", out var n) ? LooseString(n) : "";
            }
            var quoteText = data.TryGetProperty("message", out _)
                ? ExtractPlainText(data.GetRawText())
                : "";
            if (quoteText.Length == 0 && data.TryGetProperty("raw_message", out var raw) && raw.ValueKind == JsonValueKind.String)
                quoteText = raw.GetString() ?? "";
            if (quoteText.Length == 0 && (quoteAttachments.Count > 0 || quoteImages.Count > 0))
                quoteText = "（被引用的附件消息）";
            if (quoteText.Length == 0) return new ReplyQuote("【引用了某条消息（内容为空）】", quoteAttachments, quoteImages);
            if (quoteText.Length > 300) quoteText = quoteText[..300] + "…";
            return new ReplyQuote(
                "【引用原消息】" + (senderName.Length > 0 ? senderName + "：" : "") + quoteText,
                quoteAttachments,
                quoteImages);
        }
        catch (Exception ex)
        {
            Log($"[引用] 解析失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>调用 OneBot API（echo 应答，10 秒超时）；失败返回 (null, 原因)。</summary>
    private async Task<(JsonElement? Resp, string? Error)> CallApiAsync(string action, object parameters, CancellationToken ct = default)
    {
        var socket = _socket;
        if (socket is null) return (null, "连接未就绪");
        var echo = $"echo-{Interlocked.Increment(ref _echoSeq)}";
        var payload = new { action, @params = parameters, echo };
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[echo] = tcs;
        try
        {
            await socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)),
                WebSocketMessageType.Text, true, ct);
            var done = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(10), ct));
            if (done != tcs.Task)
            {
                Log($"API {action} 超时");
                return (null, "接口调用超时");
            }
            var response = tcs.Task.Result;
            if (response.TryGetProperty("retcode", out var rc) && rc.ValueKind == JsonValueKind.Number && rc.GetInt32() != 0)
            {
                var message = response.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                    ? m.GetString() ?? ""
                    : "";
                var raw = response.GetRawText();
                Log($"API {action} 失败：{raw[..Math.Min(200, raw.Length)]}");
                return (null, message.Length > 0 ? message : $"retcode {rc.GetInt32()}");
            }
            return (response, null);
        }
        catch (Exception ex)
        {
            Log($"API {action} 异常：{ex.Message}");
            return (null, ex.Message);
        }
        finally
        {
            _pending.TryRemove(echo, out _);
        }
    }

    /// <summary>OneBot11 文件段（file 类型消息段）信息。</summary>
    public sealed record FileSegment(string Name, string FileId, long Size, long Busid, string? DirectUrl);

    /// <summary>解析消息 JSON 中的文件段（NapCat：data 含 file/file_id/file_size/busid，个别版本带 url；数字/字符串字段均兼容）。</summary>
    public static List<FileSegment> ExtractFileSegments(string rawJson)
    {
        var result = new List<FileSegment>();
        using var doc = JsonDocument.Parse(rawJson);
        if (!doc.RootElement.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var seg in message.EnumerateArray())
        {
            if (!seg.TryGetProperty("type", out var type) || type.GetString() != "file") continue;
            if (!seg.TryGetProperty("data", out var data)) continue;
            var name = data.TryGetProperty("file", out var f) ? LooseString(f) : "附件";
            var fileId = data.TryGetProperty("file_id", out var fid) ? LooseString(fid) : "";
            var size = LooseInt64(data, "file_size");
            var busid = LooseInt64(data, "busid");
            var url = data.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
            result.Add(new FileSegment(name, fileId, size, busid, url));
        }
        return result;
    }

    /// <summary>宽松取整数（数字或字符串均兼容，缺失/非法返回 0）。</summary>
    private static long LooseInt64(JsonElement obj, string property)
    {
        if (!obj.TryGetProperty(property, out var el)) return 0;
        return el.ValueKind switch
        {
            JsonValueKind.Number => el.TryGetInt64(out var n) ? n : 0,
            JsonValueKind.String => long.TryParse(el.GetString(), out var parsed) ? parsed : 0,
            _ => 0,
        };
    }

    /// <summary>解析消息 JSON 中的图片段 URL（NapCat 推送：data.url 为 QQ CDN 链接）。</summary>
    public static List<string> ExtractImageUrls(string rawJson)
    {
        var result = new List<string>();
        using var doc = JsonDocument.Parse(rawJson);
        if (!doc.RootElement.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var seg in message.EnumerateArray())
        {
            if (!seg.TryGetProperty("type", out var type) || type.GetString() != "image") continue;
            if (!seg.TryGetProperty("data", out var data)) continue;
            if (!data.TryGetProperty("url", out var u) || u.ValueKind != JsonValueKind.String) continue;
            var url = u.GetString();
            if (!string.IsNullOrWhiteSpace(url) && url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                result.Add(url);
        }
        return result;
    }

    private void Log(string message) => ConnectionLog?.Invoke(this, $"[OneBot11] {message}");

    /// <summary>OneBot11 消息段数组 → 纯文本（保留 @ 与图片标记）。</summary>
    public static string ExtractPlainText(string rawJson)
    {
        using var doc = JsonDocument.Parse(rawJson);
        if (!doc.RootElement.TryGetProperty("message", out var message)) return "";
        if (message.ValueKind != JsonValueKind.Array) return message.GetString() ?? "";
        var sb = new StringBuilder();
        foreach (var seg in message.EnumerateArray())
        {
            if (!seg.TryGetProperty("type", out var type)) continue;
            switch (type.GetString())
            {
                case "text":
                    sb.Append(seg.TryGetProperty("data", out var d) && d.TryGetProperty("text", out var t) ? LooseString(t) : "");
                    break;
                case "at":
                    sb.Append(seg.TryGetProperty("data", out var ad) && ad.TryGetProperty("qq", out var qq) ? $"@{LooseString(qq)} " : "@");
                    break;
                case "image":
                    sb.Append("[图片]");
                    break;
                case "file":
                    sb.Append(seg.TryGetProperty("data", out var fd) && fd.TryGetProperty("file", out var fname)
                        ? $"[文件: {LooseString(fname)}]"
                        : "[文件]");
                    break;
                default:
                    sb.Append($"[{type.GetString()}]");
                    break;
            }
        }
        return sb.ToString().Trim();
    }

    /// <summary>容错取值：OneBot11 字段类型不稳定（qq 可能是数字也可能是字符串）。</summary>
    private static string LooseString(JsonElement el) =>
        el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : el.ToString();
}
