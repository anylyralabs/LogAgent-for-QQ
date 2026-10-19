using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace LogAgent.Headless;

/// <summary>
/// 内嵌的 OpenAI 兼容假服务（仅测试/自检用）：<c>selftest</c> 用它替代真实模型端点，
/// 这样 CI 与离线环境也能跑通「配置 → 路由 → Agent → 回复」全链路，不联网。
/// </summary>
internal sealed class EmbeddedOpenAiStub : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();

    /// <summary>服务基地址（形如 http://127.0.0.1:12345/v1）。绑定端口后才有值。</summary>
    public string BaseUrl { get; private set; } = "";

    /// <summary>固定回复内容（自检断言用）。</summary>
    public string ReplyText { get; set; } = "自检回复：正常";

    /// <summary>最近一次 chat/completions 请求体（自检断言用）。</summary>
    public string? LastRequestBody { get; private set; }

    public static EmbeddedOpenAiStub Start()
    {
        var stub = new EmbeddedOpenAiStub();
        stub._listener.Start();
        var port = ((IPEndPoint)stub._listener.LocalEndpoint).Port;
        stub.BaseUrl = $"http://127.0.0.1:{port}/v1";
        _ = Task.Run(stub.AcceptLoopAsync);
        return stub;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch (Exception) { return; }
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using var _ = client;
        try
        {
            await using var stream = client.GetStream();
            var (method, path, body) = await ReadRequestAsync(stream);
            if (method == "GET" && path.Contains("/models"))
            {
                await WriteJsonAsync(stream, 200, new
                {
                    @object = "list",
                    data = new[] { new { id = "selftest-model", @object = "model" } },
                });
                return;
            }
            if (method == "POST" && path.Contains("/chat/completions"))
            {
                LastRequestBody = body;
                await WriteJsonAsync(stream, 200, new
                {
                    id = "chatcmpl-selftest",
                    @object = "chat.completion",
                    choices = new[]
                    {
                        new
                        {
                            index = 0,
                            message = new { role = "assistant", content = ReplyText },
                            finish_reason = "stop",
                        },
                    },
                    usage = new { prompt_tokens = 12, completion_tokens = 6, total_tokens = 18 },
                });
                return;
            }
            if (method == "POST" && path.Contains("/embeddings"))
            {
                await WriteJsonAsync(stream, 200, new
                {
                    data = new[] { new { index = 0, embedding = new[] { 0.1, 0.2, 0.3 } } },
                });
                return;
            }
            await WriteJsonAsync(stream, 404, new { error = "not found" });
        }
        catch (Exception)
        {
            // 客户端断开等：忽略
        }
    }

    private static async Task<(string Method, string Path, string Body)> ReadRequestAsync(NetworkStream stream)
    {
        var buffer = new List<byte>(4096);
        var one = new byte[1];
        while (true)
        {
            var n = await stream.ReadAsync(one);
            if (n == 0) break;
            buffer.Add(one[0]);
            if (buffer.Count >= 4 && buffer[^4] == 13 && buffer[^3] == 10 && buffer[^2] == 13 && buffer[^1] == 10)
                break;
        }
        var headerText = Encoding.ASCII.GetString(buffer.ToArray());
        var lines = headerText.Split("\r\n");
        var first = lines.FirstOrDefault() ?? "";
        var parts = first.Split(' ');
        var method = parts.Length > 0 ? parts[0] : "";
        var path = parts.Length > 1 ? parts[1] : "";

        var contentLength = 0;
        foreach (var line in lines)
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                int.TryParse(line["Content-Length:".Length..].Trim(), out contentLength);
        }

        var body = "";
        if (contentLength > 0)
        {
            var bytes = new byte[contentLength];
            var read = 0;
            while (read < contentLength)
            {
                var n = await stream.ReadAsync(bytes.AsMemory(read, contentLength - read));
                if (n == 0) break;
                read += n;
            }
            body = Encoding.UTF8.GetString(bytes, 0, read);
        }
        return (method, path, body);
    }

    private static async Task WriteJsonAsync(NetworkStream stream, int status, object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        var head = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Not Found")}\r\n" +
                   "Content-Type: application/json; charset=utf-8\r\n" +
                   $"Content-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch (Exception) { /* 已停止 */ }
        _cts.Dispose();
        return ValueTask.CompletedTask;
    }
}
