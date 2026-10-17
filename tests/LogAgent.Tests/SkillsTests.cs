using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LogAgent.Core;
using LogAgent.Memory;
using LogAgent.Routing;
using LogAgent.Shared.Chat;
using LogAgent.Shared.Configuration;
using LogAgent.Shared.Messaging;
using LogAgent.Skills;
using Xunit;

namespace LogAgent.Tests;

// ─────────────────────────── 清单解析 ───────────────────────────

public class SkillManifestTests
{
    [Fact]
    public void Parse_ValidManifest_ReadsToolsAndSettings()
    {
        var manifest = SkillManifest.Parse("""
            {
              "id": "demo", "name": "演示技能", "version": "1.0.0", "type": "http",
              "baseUrl": "https://api.example.com",
              "settings": [ { "key": "source", "label": "来源", "default": "test" } ],
              "tools": [ { "name": "demo_tool", "description": "测试", "method": "POST", "path": "/x",
                           "bodyTemplate": { "text": "{content}" },
                           "params": [ { "name": "content", "required": true } ] } ]
            }
            """);
        Assert.NotNull(manifest);
        Assert.Equal("demo", manifest!.Id);
        Assert.Single(manifest.Tools);
        Assert.True(manifest.Tools[0].Params[0].Required);
        Assert.Equal("test", manifest.Settings[0].Default);
    }

    [Fact]
    public void Parse_InvalidManifest_ReturnsNull()
    {
        Assert.Null(SkillManifest.Parse("{}"));        // 无 id
        Assert.Null(SkillManifest.Parse("not json"));  // 非 JSON
    }

    [Fact]
    public void Parse_ManifestWithoutTools_IsValidButToolLess()
    {
        // 无工具的清单仍可加载（作为纯描述条目）；Tools 为空
        var manifest = SkillManifest.Parse("""{"id":"x","description":"说明","tools":[]}""");
        Assert.NotNull(manifest);
        Assert.Empty(manifest!.Tools);
    }

    [Fact]
    public void Parse_BuildsParametersSchema()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"la-skill-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "demo"));
        File.WriteAllText(Path.Combine(dir, "demo", "skill.json"), """
            { "id": "demo", "name": "D", "baseUrl": "http://x",
              "tools": [ { "name": "t1", "description": "d", "path": "/a",
                           "params": [ { "name": "id", "required": true }, { "name": "k" } ] } ] }
            """);
        var registry = new SkillRegistry(dir, Path.Combine(dir, "none"));
        registry.Reload();
        var tools = registry.BuildToolDefinitions(new SkillsConfig());
        var tool = Assert.Single(tools);
        Assert.Equal("t1", tool.Name);
        Assert.Contains("\"id\"", tool.ParametersJson);
        Assert.Contains("\"required\":[\"id\"]", tool.ParametersJson);
        Directory.Delete(dir, true);
    }
}

// ─────────────────────────── 工具执行（本地迷你 HTTP 服务） ───────────────────────────

public class SkillToolExecutionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"la-skill-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private SkillRegistry MakeRegistry(MiniHttpServer server)
    {
        var skillDir = Path.Combine(_dir, "demo");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "skill.json"), $$"""
            {
              "id": "demo", "name": "演示", "type": "http",
              "baseUrl": "http://127.0.0.1:{{server.Port}}",
              "settings": [ { "key": "source", "label": "来源", "default": "test-tag" } ],
              "tools": [
                { "name": "demo_upload", "description": "上传", "method": "POST", "path": "/v1/log",
                  "headers": { "Content-Type": "application/json" },
                  "bodyTemplate": { "content": "{content}", "source": "{setting.source}" },
                  "params": [ { "name": "content", "required": true } ] },
                { "name": "demo_get", "description": "查询", "method": "GET", "path": "/v1/raw/{id}",
                  "params": [ { "name": "id", "required": true } ] },
                { "name": "demo_sse", "description": "流式", "method": "GET", "path": "/v1/ai/{id}",
                  "params": [ { "name": "id", "required": true } ] }
              ]
            }
            """);
        var registry = new SkillRegistry(Path.Combine(_dir, "builtin-none"), _dir);
        registry.Reload();
        return registry;
    }

    [Fact]
    public async Task ExecuteTool_SubstitutesArgsAndSettings()
    {
        string? capturedBody = null;
        string? capturedPath = null;
        using var server = new MiniHttpServer((method, path, body) =>
        {
            capturedBody = body;
            capturedPath = path;
            return ("200 OK", "application/json", "{\"ok\":true}");
        });
        var registry = MakeRegistry(server);

        var result = await registry.ExecuteToolAsync("demo_upload", "{\"content\":\"你好日志\"}", new SkillsConfig());
        Assert.Equal("{\"ok\":true}", result);
        Assert.Contains("你好日志", capturedBody);
        Assert.Contains("test-tag", capturedBody);
        Assert.Equal("/v1/log", capturedPath);
    }

    [Fact]
    public async Task ExecuteTool_MissingRequiredArg_ReturnsErrorText()
    {
        using var server = new MiniHttpServer((_, _, _) => ("200 OK", "text/plain", "x"));
        var registry = MakeRegistry(server);
        var result = await registry.ExecuteToolAsync("demo_upload", "{}", new SkillsConfig());
        Assert.StartsWith("错误：缺少必填参数", result);
    }

    [Fact]
    public async Task ExecuteTool_UnknownTool_ReturnsErrorText()
    {
        using var server = new MiniHttpServer((_, _, _) => ("200 OK", "text/plain", "x"));
        var registry = MakeRegistry(server);
        var result = await registry.ExecuteToolAsync("nope", "{}", new SkillsConfig());
        Assert.StartsWith("错误：未找到工具", result);
    }

    [Fact]
    public async Task ExecuteTool_DisabledSkill_IsNotAvailable()
    {
        using var server = new MiniHttpServer((_, _, _) => ("200 OK", "text/plain", "x"));
        var registry = MakeRegistry(server);
        var config = new SkillsConfig { DisabledSkills = ["demo"] };
        Assert.Empty(registry.BuildToolDefinitions(config));
        var result = await registry.ExecuteToolAsync("demo_upload", "{\"content\":\"x\"}", config);
        Assert.StartsWith("错误：未找到工具", result);
    }

    [Fact]
    public async Task ExecuteTool_PathPlaceholder_IsSubstituted()
    {
        string? capturedPath = null;
        using var server = new MiniHttpServer((_, path, _) =>
        {
            capturedPath = path;
            return ("200 OK", "application/json", "raw-log");
        });
        var registry = MakeRegistry(server);
        var result = await registry.ExecuteToolAsync("demo_get", "{\"id\":\"p9n97Ui\"}", new SkillsConfig());
        Assert.Equal("raw-log", result);
        Assert.Equal("/v1/raw/p9n97Ui", capturedPath);
    }

    [Fact]
    public async Task ExecuteTool_Sse_AggregatesContentAndSurfacesErrors()
    {
        using var server = new MiniHttpServer((_, path, _) => path.Contains("/v1/ai/ok")
            ? ("200 OK", "text/event-stream",
                "event: status\ndata: {\"type\":\"queued\"}\n\nevent: message\ndata: {\"content\":\"你好\"}\n\nevent: message\ndata: {\"content\":\"世界\"}\n\n")
            : ("200 OK", "text/event-stream",
                "event: error\ndata: {\"error\":\"当前账户暂无生效套餐\"}\n\n"));
        var registry = MakeRegistry(server);

        var ok = await registry.ExecuteToolAsync("demo_sse", "{\"id\":\"ok\"}", new SkillsConfig());
        Assert.Equal("你好世界", ok);

        var err = await registry.ExecuteToolAsync("demo_sse", "{\"id\":\"bad\"}", new SkillsConfig());
        Assert.Contains("云端返回错误", err);
        Assert.Contains("套餐", err);
    }
}

// ─────────────────────────── 商店目录 ───────────────────────────

public class SkillCatalogTests
{
    [Fact]
    public void Parse_SkipsBadEntries()
    {
        var entries = SkillCatalog.Parse("""
            { "skills": [
                { "id": "a", "name": "A", "version": "1.0.0", "description": "d", "downloadUrl": "https://x/a.zip" },
                { "name": "no-id" },
                { "id": "b", "name": "B" }
            ] }
            """);
        Assert.Equal(2, entries.Count);
        Assert.Equal("https://x/a.zip", entries[0].DownloadUrl);
    }

    [Fact]
    public void Parse_NoSkillsKey_ReturnsEmpty()
    {
        Assert.Empty(SkillCatalog.Parse("{}"));
    }
}

// ─────────────────────────── 工具调用循环（Agent 级） ───────────────────────────

internal sealed class RecordingToolHost : ISkillToolHost
{
    public List<(string Name, string Args)> Invoked { get; } = [];

    public IReadOnlyList<ChatToolDefinition> BuildToolDefinitions(SkillsConfig config)
        => [new ChatToolDefinition("demo_tool", "测试工具", "{\"type\":\"object\",\"properties\":{}}")];

    public string BuildPromptSkillsBlock(SkillsConfig config) => "";

    public Task<string> ExecuteToolAsync(string toolName, string argumentsJson, SkillsConfig config, CancellationToken ct)
    {
        Invoked.Add((toolName, argumentsJson));
        return Task.FromResult("工具结果: ok");
    }
}

public class ToolLoopTests
{
    private static GroupContextAgent MakeAgent(ModelRouter router, ISkillToolHost host, SkillsConfig config)
        => new(router, null, null, host, config);

    private static BotMessage Msg(string text) => new()
    {
        MessageId = "m1", ChatId = "777", ChatType = ChatType.Group,
        SenderId = "1", SenderName = "柠枺", Text = text, MentionedMe = true,
    };

    [Fact]
    public async Task Reply_ModelCallsTool_ThenAnswers()
    {
        var calls = 0;
        var appConfig = new AppConfig
        {
            Providers = [new ProviderConfig { Id = "p1", Name = "P1", ChatModel = "m", Enabled = true }],
            Rules = [new RouterRule { TaskType = TaskType.Chat, ProviderChain = ["p1"] }],
        };
        var router = new ModelRouter(appConfig, _ => new FakeClient("p1", true)
        {
            ResultOverride = _ =>
            {
                calls++;
                return calls == 1
                    ? new ChatResult("", "p1", "m", 1, 1, TimeSpan.Zero,
                        ToolCalls: [new ChatToolCall("c1", "demo_tool", "{\"x\":\"1\"}")],
                        FinishReason: "tool_calls")
                    : new ChatResult("最终回答", "p1", "m", 1, 1, TimeSpan.Zero, FinishReason: "stop");
            },
        });
        var host = new RecordingToolHost();
        var agent = MakeAgent(router, host, new SkillsConfig());

        var reply = await agent.ReplyAsync(Msg("帮我处理一下"), new SessionContext("s", "c", [], []));

        Assert.Equal("最终回答", reply);
        var invoked = Assert.Single(host.Invoked);
        Assert.Equal("demo_tool", invoked.Name);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Reply_ToolsDisabled_NoToolsExposed()
    {
        var appConfig = new AppConfig
        {
            Providers = [new ProviderConfig { Id = "p1", Name = "P1", ChatModel = "m", Enabled = true }],
            Rules = [new RouterRule { TaskType = TaskType.Chat, ProviderChain = ["p1"] }],
        };
        IReadOnlyList<ChatToolDefinition>? seenTools = null;
        var router = new ModelRouter(appConfig, _ => new FakeClient("p1", true)
        {
            ResultOverride = req =>
            {
                seenTools = req.Tools;
                return new ChatResult("回答", "p1", "m", 1, 1, TimeSpan.Zero, FinishReason: "stop");
            },
        });
        var agent = MakeAgent(router, new RecordingToolHost(), new SkillsConfig { Enabled = false });

        var reply = await agent.ReplyAsync(Msg("你好"), new SessionContext("s", "c", [], []));
        Assert.Equal("回答", reply);
        Assert.True(seenTools is null || seenTools.Count == 0);
    }
}

// ─────────────────────────── OpenAI 客户端：工具序列化 / 解析 ───────────────────────────

internal sealed class CaptureHandler : HttpMessageHandler
{
    public string? LastRequestBody { get; private set; }
    public string ResponseJson { get; set; } =
        """{"choices":[{"message":{"content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1}}""";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ResponseJson, Encoding.UTF8, "application/json"),
        };
    }
}

public class OpenAiToolCallingTests
{
    private static OpenAiCompatClient MakeClient(CaptureHandler handler)
        => new(new ProviderConfig { Id = "p1", ChatModel = "m", Endpoint = "http://unit.test/v1" },
            new HttpClient(handler));

    [Fact]
    public async Task Request_SerializesToolsAndToolMessages()
    {
        var handler = new CaptureHandler();
        var client = MakeClient(handler);
        await client.CompleteAsync(new ChatRequest(
            [
                ChatTurn.User("u"),
                ChatTurn.AssistantWithTools("", [new ChatToolCall("c1", "demo", "{}")]),
                ChatTurn.Tool("c1", "工具结果"),
            ],
            Tools: [new ChatToolDefinition("demo", "d", "{\"type\":\"object\",\"properties\":{}}")]));

        Assert.NotNull(handler.LastRequestBody);
        Assert.Contains("\"tools\"", handler.LastRequestBody);
        Assert.Contains("\"tool_calls\"", handler.LastRequestBody);
        Assert.Contains("\"tool_call_id\":\"c1\"", handler.LastRequestBody);
        Assert.Contains("工具结果", handler.LastRequestBody);
    }

    [Fact]
    public async Task Response_ParsesToolCalls()
    {
        var handler = new CaptureHandler
        {
            ResponseJson = """
                {"choices":[{"message":{"content":null,"tool_calls":[
                    {"id":"c9","type":"function","function":{"name":"demo_tool","arguments":"{\"a\":1}"}}
                ]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":2,"completion_tokens":3}}
                """,
        };
        var client = MakeClient(handler);
        var result = await client.CompleteAsync(new ChatRequest([ChatTurn.User("u")]));

        Assert.Equal("", result.Content);
        Assert.Equal("tool_calls", result.FinishReason);
        var call = Assert.Single(result.ToolCalls!);
        Assert.Equal("c9", call.Id);
        Assert.Equal("demo_tool", call.Name);
        Assert.Contains("\"a\":1", call.Arguments);
    }
}

// ─────────────────────────── 迷你 HTTP 服务器（测试用） ───────────────────────────

/// <summary>最小 HTTP/1.1 服务：给定 (method, path, body) 返回 (status, contentType, body)。</summary>
internal sealed class MiniHttpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly Func<string, string, string, (string Status, string ContentType, string Body)> _handler;
    private readonly CancellationTokenSource _cts = new();

    public int Port { get; }

    public MiniHttpServer(Func<string, string, string, (string, string, string)> handler)
    {
        _handler = handler;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (Exception)
            {
                return;
            }
            _ = Task.Run(() => HandleClientAsync(client));
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using var _ = client;
        try
        {
            var stream = client.GetStream();
            var headerBytes = new List<byte>();
            var one = new byte[1];
            while (true)
            {
                var n = await stream.ReadAsync(one);
                if (n == 0) return;
                headerBytes.Add(one[0]);
                if (headerBytes.Count >= 4 && headerBytes[^4] == 13 && headerBytes[^3] == 10
                    && headerBytes[^2] == 13 && headerBytes[^1] == 10)
                    break;
            }
            var headerText = Encoding.ASCII.GetString(headerBytes.ToArray());
            var lines = headerText.Split("\r\n");
            var parts = lines[0].Split(' ');
            var method = parts[0];
            var path = parts.Length > 1 ? parts[1] : "/";
            var contentLength = 0;
            foreach (var line in lines)
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(line[15..].Trim(), out contentLength);
            }
            var bodyBytes = new byte[contentLength];
            var offset = 0;
            while (offset < contentLength)
            {
                var n = await stream.ReadAsync(bodyBytes.AsMemory(offset, contentLength - offset));
                if (n == 0) break;
                offset += n;
            }
            var body = Encoding.UTF8.GetString(bodyBytes, 0, offset);

            var (status, contentType, responseBody) = _handler(method, path, body);
            var payload = Encoding.UTF8.GetBytes(responseBody);
            var header = $"HTTP/1.1 {status}\r\nContent-Type: {contentType}; charset=utf-8\r\n"
                         + $"Content-Length: {payload.Length}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(header));
            await stream.WriteAsync(payload);
            await stream.FlushAsync();
        }
        catch (Exception)
        {
            // 连接中断：测试服务器忽略
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _cts.Dispose();
    }
}
