using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using LogAgent.Connections;
using LogAgent.Shared.Configuration;
using LogAgent.Shared.Messaging;
using Xunit;

namespace LogAgent.Tests;

/// <summary>OneBot11 适配器集成测试：起真 WS 服务端 + 模拟 NapCat 客户端。</summary>
public class OneBot11ConnectionTests : IAsyncDisposable
{
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private readonly OneBot11Connection _conn;
    private readonly ClientWebSocket _fakeNapCat = new();
    private readonly string _url;

    public OneBot11ConnectionTests()
    {
        var config = new OneBot11Config { Host = "127.0.0.1", Port = FreePort(), Path = "/onebot/v11/ws", Token = "t123" };
        _conn = new OneBot11Connection(config);
        _url = $"ws://127.0.0.1:{config.Port}/onebot/v11/ws";
    }

    private async Task<ClientWebSocket> ConnectAsync()
    {
        _fakeNapCat.Options.SetRequestHeader("Authorization", "Bearer t123");
        await _conn.StartAsync();
        await _fakeNapCat.ConnectAsync(new Uri(_url), CancellationToken.None);
        // 等 Kestrel accept 完成
        await Task.Delay(200);
        return _fakeNapCat;
    }

    private static async Task SendWsAsync(ClientWebSocket ws, string json)
        => await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<string> RecvWsAsync(ClientWebSocket ws)
    {
        var buffer = new byte[32 * 1024];
        using var ms = new MemoryStream();
        WebSocketReceiveResult r;
        do
        {
            r = await ws.ReceiveAsync(buffer, CancellationToken.None);
            ms.Write(buffer, 0, r.Count);
        } while (!r.EndOfMessage);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    [Fact]
    public async Task GroupMessageEvent_ParsesToBotMessage()
    {
        var logs = new List<string>();
        _conn.ConnectionLog += (_, line) => logs.Add(line);
        var ws = await ConnectAsync();
        BotMessage? received = null;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _conn.MessageReceived += (_, e) => { received = e.Message; gate.TrySetResult(); };

        await SendWsAsync(ws, """
            {"post_type":"meta_event","meta_event_type":"lifecycle","sub_type":"connect","self_id":10086}
            """);
        await SendWsAsync(ws, """
            {"post_type":"message","message_type":"group","time":1,"self_id":10086,
             "message":[{"type":"at","data":{"qq":10086}},{"type":"text","data":{"text":" 你好"}}],
             "raw_message":"你好","message_id":42,"user_id":12345,"group_id":777,
             "sender":{"user_id":12345,"nickname":"柠枺","card":"群名片"}}
            """);

        try
        {
            await gate.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"未收到消息事件。服务端日志：\n{string.Join("\n", logs)}");
        }
        Assert.NotNull(received);
        Assert.Equal("777", received!.ChatId);
        Assert.Equal(ChatType.Group, received.ChatType);
        Assert.Equal("12345", received.SenderId);
        Assert.Equal("群名片", received.SenderName);
        Assert.Contains("你好", received.Text);
        Assert.True(received.MentionedMe);
        Assert.Equal("42", received.MessageId);
    }

    [Fact]
    public async Task SendTextAsync_Group_SendsCorrectActionAndMatchesEcho()
    {
        var ws = await ConnectAsync();
        // 消费方：读 API 请求，回 echo 应答
        var consumer = Task.Run(async () =>
        {
            var req = await RecvWsAsync(ws);
            using var doc = JsonDocument.Parse(req);
            var root = doc.RootElement;
            Assert.Equal("send_group_msg", root.GetProperty("action").GetString());
            var ps = root.GetProperty("params");
            Assert.Equal(777, ps.GetProperty("group_id").GetInt64());
            Assert.Equal("测试回复", ps.GetProperty("message").GetString());
            var echo = root.GetProperty("echo").GetString()!;
            await SendWsAsync(ws, JsonSerializer.Serialize(
                new { echo, status = new { retcode = 0 }, data = new { message_id = 99 } }));
        });

        await _conn.SendTextAsync("777", ChatType.Group, "测试回复", "42");
        await consumer.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task TokenMismatch_Rejected()
    {
        await _conn.StartAsync();
        var bad = new ClientWebSocket();
        bad.Options.SetRequestHeader("Authorization", "Bearer wrong");
        await Assert.ThrowsAnyAsync<WebSocketException>(
            () => bad.ConnectAsync(new Uri(_url), CancellationToken.None));
    }

    public async ValueTask DisposeAsync()
    {
        if (_fakeNapCat.State == WebSocketState.Open)
            await _fakeNapCat.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        _fakeNapCat.Dispose();
        await _conn.DisposeAsync();
    }
}
