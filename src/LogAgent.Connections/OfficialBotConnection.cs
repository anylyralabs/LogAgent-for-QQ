using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using LogAgent.Shared.Configuration;
using LogAgent.Shared.Connections;
using LogAgent.Shared.Messaging;

namespace LogAgent.Connections;

/// <summary>
/// QQ 官方机器人适配器（开放平台 API v2）：
/// access_token → gateway → WS（op10 Hello / op2 Identify / op1 心跳 / op0 事件 / op6 Resume）
/// 被动回复：POST /v2/groups/{openid}/messages 或 /v2/users/{openid}/messages，携带 msg_id（5 分钟窗口、限 5 次）。
/// 规划 3.1B。未经真机联调，凭证链路以官方文档为准（bot.q.qq.com）。
/// </summary>
public sealed class OfficialBotConnection : IBotConnection
{
    private static readonly Uri TokenEndpoint = new("https://bots.qq.com/app/getAppAccessToken");
    private static readonly Uri ProdBase = new("https://api.sgroup.qq.com");
    private static readonly Uri SandboxBase = new("https://sandbox.api.sgroup.qq.com");

    private readonly OfficialBotConfig _config;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _worker;

    private string? _accessToken;
    private DateTimeOffset _tokenExpiresAt;
    private string? _sessionId;
    private long _lastSeq;
    private readonly ConcurrentDictionary<string, byte> _rateWindow = new(); // M1 简易限频
    private readonly ConcurrentDictionary<string, int> _replyCount = [];     // 每个 msg_id 回复次数（≤5）

    public string Name => $"QQ官方Bot ({_config.AppId}{(_config.Sandbox ? " 沙箱" : "")})";
    public bool IsConnected => _ws is { State: WebSocketState.Open };

    public event EventHandler<MessageReceivedEventArgs>? MessageReceived;
    public event EventHandler<string>? ConnectionLog;

    public OfficialBotConnection(OfficialBotConfig config) => _config = config;

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
        if (_ws is { State: WebSocketState.Open })
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "shutdown", ct); }
            catch (WebSocketException) { }
        }
        if (_worker is not null) { try { await _worker; } catch (OperationCanceledException) { } }
        Log("官方 Bot 已停止");
    }

    public async Task SendTextAsync(string chatId, ChatType chatType, string text, string? replyToMessageId = null, CancellationToken ct = default)
    {
        if (replyToMessageId is null)
        {
            // 主动消息受用户开关与配额限制，M1 仅记录告警
            Log("警告：官方 Bot 群聊仅支持被动回复（需 msg_id），主动消息可能失败");
        }
        else
        {
            var used = _replyCount.AddOrUpdate(replyToMessageId, 1, (_, n) => n + 1);
            if (used > 5)
            {
                Log($"msg_id={replyToMessageId} 回复次数超限（5 次），放弃发送");
                return;
            }
        }

        // 全局 800 次/分钟简易限频：滑窗内超过 700 暂缓
        while (_rateWindow.Count > 700)
        {
            _rateWindow.Clear(); // M1 简化：整窗清空；M5 换令牌桶
            break;
        }

        var path = chatType == ChatType.Group
            ? $"/v2/groups/{chatId}/messages"
            : $"/v2/users/{chatId}/messages";
        var body = new Dictionary<string, object?>
        {
            ["msg_type"] = 0,
            ["content"] = text,
            ["msg_id"] = replyToMessageId,
            ["msg_seq"] = _replyCount.TryGetValue(replyToMessageId ?? "", out var seq) ? seq : 1,
        };
        using var resp = await _http.PostAsJsonAsync(new Uri(ApiBase, path), body, ct);
        if (!resp.IsSuccessStatusCode)
            Log($"发送失败 HTTP {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync(ct)}");
    }

    public async ValueTask DisposeAsync() { await StopAsync(); _http.Dispose(); }

    private async Task RunWsAsync(string gatewayUrl, CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectOnceAsync(gatewayUrl, ct);
                backoff = TimeSpan.FromSeconds(1);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log($"WS 断开：{ex.Message}，{backoff.TotalSeconds}s 后重连");
                await Task.Delay(backoff, ct);
                backoff = backoff.TotalSeconds < 60 ? TimeSpan.FromMilliseconds(backoff.TotalMilliseconds * 2) : TimeSpan.FromSeconds(60);
            }
        }
    }

    private async Task ConnectOnceAsync(string gatewayUrl, CancellationToken ct)
    {
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
                    intents = 1 << 25, // GROUP_AND_C2C_EVENT：群@消息 + 单聊
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
                    _sessionId = payload.GetProperty("d").GetProperty("session_id").GetString();
                    Log($"鉴权成功，session={_sessionId?[..Math.Min(8, _sessionId.Length)]}…");
                }
                else if (type is "GROUP_AT_MESSAGE_CREATE" or "C2C_MESSAGE_CREATE")
                {
                    var msg = ParseEvent(payload, type == "GROUP_AT_MESSAGE_CREATE");
                    if (msg is not null)
                    {
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
                Log("会话无效，将重新 Identify");
                break;
        }
    }

    private BotMessage? ParseEvent(JsonElement payload, bool isGroup)
    {
        var d = payload.GetProperty("d");
        var content = d.TryGetProperty("content", out var c) ? c.GetString()?.Trim() : null;
        var attachments = ParseAttachments(d);
        if (string.IsNullOrEmpty(content) && attachments.Count == 0) return null;
        var chatId = isGroup
            ? d.TryGetProperty("group_openid", out var g) ? g.GetString() : null
            : d.TryGetProperty("openid", out var o) ? o.GetString() : null;
        if (chatId is null) return null;
        var author = d.TryGetProperty("author", out var a) ? a : (JsonElement?)null;
        var messageId = payload.TryGetProperty("id", out var id) ? id.GetString() : null;

        return new BotMessage
        {
            MessageId = messageId ?? Guid.NewGuid().ToString("N"),
            ChatId = chatId,
            ChatType = isGroup ? ChatType.Group : ChatType.Private,
            SenderId = author?.TryGetProperty("member_openid", out var mo) == true ? mo.GetString() ?? "" :
                       author?.TryGetProperty("id", out var uid) == true ? uid.GetString() ?? "" : "",
            SenderName = "",
            Text = content ?? "",
            MentionedMe = isGroup, // GROUP_AT_MESSAGE_CREATE 天然是 @ 机器人
            Timestamp = DateTimeOffset.Now,
            RawJson = payload.GetRawText(),
            Attachments = attachments.Count > 0 ? attachments : null,
        };
    }

    /// <summary>解析官方 Bot 消息附件（attachments[]：filename/url/size/content_type；url 有效期约 10 分钟需尽快下载）。</summary>
    public static List<MessageAttachment> ParseAttachments(JsonElement d)
    {
        var result = new List<MessageAttachment>();
        if (!d.TryGetProperty("attachments", out var attachments) || attachments.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var att in attachments.EnumerateArray())
        {
            if (!att.TryGetProperty("url", out var urlEl) || urlEl.ValueKind != JsonValueKind.String) continue;
            var url = urlEl.GetString();
            if (string.IsNullOrEmpty(url)) continue;
            var name = att.TryGetProperty("filename", out var fn) && fn.ValueKind == JsonValueKind.String
                ? fn.GetString()
                : null;
            if (string.IsNullOrEmpty(name))
            {
                var contentType = att.TryGetProperty("content_type", out var ctv) && ctv.ValueKind == JsonValueKind.String
                    ? ctv.GetString()
                    : null;
                name = $"附件-{contentType ?? "unknown"}";
            }
            var size = att.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number ? sz.GetInt64() : 0;
            result.Add(new MessageAttachment(name!, url!, size));
        }
        return result;
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

    private async Task RefreshTokenAsync(CancellationToken ct)
    {
        if (_accessToken is not null && DateTimeOffset.UtcNow < _tokenExpiresAt - TimeSpan.FromMinutes(5))
            return;
        using var resp = await _http.PostAsJsonAsync(TokenEndpoint,
            new { appId = _config.AppId, clientSecret = _config.AppSecret }, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        _accessToken = doc.RootElement.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("获取 access_token 失败");
        // 注意：官方 expires_in 返回的是字符串（"7200"），必须兼容读取
        var expiresIn = ReadInt(doc.RootElement, "expires_in", 7200);
        _tokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
        Log($"access_token 已刷新（{expiresIn}s）");
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
