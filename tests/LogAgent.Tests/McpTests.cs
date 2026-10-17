using System.Text.Json;
using System.Threading.Channels;
using LogAgent.Mcp;
using LogAgent.Shared.Configuration;

namespace LogAgent.Tests;

// ─────────────────────────── 测试基础设施 ───────────────────────────

/// <summary>
/// 内存回环传输：客户端写出 → 测试侧读取；测试侧写入 → 客户端读取。
/// 用于在无外部进程的情况下完整测试 MCP 协议交互。
/// </summary>
internal sealed class LoopbackTransport : IMcpTransport
{
    private readonly Channel<string> _toServer = Channel.CreateUnbounded<string>();
    private readonly Channel<string> _fromServer = Channel.CreateUnbounded<string>();

    public ServerSide Server { get; }

    public LoopbackTransport() => Server = new ServerSide(this);

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    public Task WriteLineAsync(string json, CancellationToken ct) => _toServer.Writer.WriteAsync(json, ct).AsTask();

    public async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        try
        {
            return await _fromServer.Reader.ReadAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    public bool IsAlive => true;
    public string Describe() => "loopback";

    public ValueTask DisposeAsync()
    {
        _fromServer.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    /// <summary>测试侧的"服务器"句柄。</summary>
    internal sealed class ServerSide(LoopbackTransport transport)
    {
        /// <summary>读取客户端发出的一行请求（10 秒保护超时）。</summary>
        public async Task<string> ReadRequestAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            return await transport._toServer.Reader.ReadAsync(cts.Token);
        }

        /// <summary>把 JSON 原样写回客户端。</summary>
        public void Respond(string json) => transport._fromServer.Writer.TryWrite(json);

        /// <summary>针对请求构造 result 响应（自动回填 id）。</summary>
        public void RespondTo(string request, string resultJson)
        {
            using var doc = JsonDocument.Parse(request);
            var id = doc.RootElement.GetProperty("id").GetRawText();
            Respond("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":" + resultJson + "}");
        }

        /// <summary>完成一次完整握手（initialize → initialized → tools/list）。</summary>
        public async Task HandleHandshakeAsync(params string[] toolJsonArray)
        {
            var init = await ReadRequestAsync();
            RespondTo(init,
                """{"protocolVersion":"2024-11-05","serverInfo":{"name":"fake-server","version":"1.0"},"capabilities":{"tools":{}}}""");
            _ = await ReadRequestAsync(); // notifications/initialized
            var list = await ReadRequestAsync();
            var tools = toolJsonArray.Length > 0 ? string.Join(",", toolJsonArray) : "";
            RespondTo(list, "{\"tools\":[" + tools + "]}");
        }
    }
}

// ─────────────────────────── 协议层测试 ───────────────────────────

public class McpProtocolTests
{
    [Fact]
    public void QualifyAndSplit_ToolNames_RoundTrip()
    {
        var qualified = McpProtocol.QualifyToolName("demo", "echo");
        Assert.Equal("mcp__demo__echo", qualified);
        var split = McpProtocol.SplitQualified(qualified);
        Assert.NotNull(split);
        Assert.Equal("demo", split!.Value.ServerId);
        Assert.Equal("echo", split.Value.ToolName);
    }

    [Fact]
    public void Qualify_SanitizesNonAlphanumeric()
    {
        Assert.Equal("mcp__my_server__do_it", McpProtocol.QualifyToolName("my-server", "do.it"));
    }

    [Fact]
    public void SplitQualified_NonMcpName_ReturnsNull()
    {
        Assert.Null(McpProtocol.SplitQualified("logshare_upload"));
        Assert.Null(McpProtocol.SplitQualified("mcp__"));
        Assert.Null(McpProtocol.SplitQualified("mcp__onlypart"));
    }

    [Fact]
    public void BuildRequest_ContainsJsonRpcFields()
    {
        var json = McpProtocol.BuildRequest(7, "tools/call", new { name = "echo" });
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("2.0", doc.RootElement.GetProperty("jsonrpc").GetString());
        Assert.Equal(7, doc.RootElement.GetProperty("id").GetInt64());
        Assert.Equal("tools/call", doc.RootElement.GetProperty("method").GetString());
        Assert.Equal("echo", doc.RootElement.GetProperty("params").GetProperty("name").GetString());
    }

    [Fact]
    public void BuildNotification_HasNoId()
    {
        var json = McpProtocol.BuildNotification("notifications/initialized", null);
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("id", out _));
        Assert.Equal("notifications/initialized", doc.RootElement.GetProperty("method").GetString());
    }

    [Fact]
    public void ParseTools_NormalAndTolerant()
    {
        var result = JsonDocument.Parse("""
            {"tools":[
              {"name":"echo","description":"回显","inputSchema":{"type":"object"}},
              {"description":"缺名跳过"},
              {"name":"add","inputSchema":{"type":"object","properties":{}}}
            ]}
            """).RootElement;
        var tools = McpProtocol.ParseTools(result);
        Assert.Equal(2, tools.Count);
        Assert.Equal("echo", tools[0].Name);
        Assert.Equal("回显", tools[0].Description);
        Assert.Equal("add", tools[1].Name);
        Assert.Contains("\"type\":\"object\"", tools[1].InputSchemaJson);
    }

    [Fact]
    public void ParseTools_MissingSchema_UsesEmptyObjectSchema()
    {
        var result = JsonDocument.Parse("""{"tools":[{"name":"t"}]}""").RootElement;
        var tools = McpProtocol.ParseTools(result);
        Assert.Single(tools);
        Assert.Contains("\"properties\"", tools[0].InputSchemaJson);
    }

    [Fact]
    public void AggregateContent_TextAndError()
    {
        var ok = JsonDocument.Parse("""{"content":[{"type":"text","text":"第一行"},{"type":"text","text":"第二行"}]}""").RootElement;
        var text = McpProtocol.AggregateContent(ok, out var isError);
        Assert.False(isError);
        Assert.Equal("第一行" + Environment.NewLine + "第二行", text);

        var bad = JsonDocument.Parse("""{"content":[{"type":"text","text":"出错了"}],"isError":true}""").RootElement;
        McpProtocol.AggregateContent(bad, out var err2);
        Assert.True(err2);
    }

    [Fact]
    public void AggregateContent_EmptyContent_Placeholder()
    {
        var empty = JsonDocument.Parse("""{"content":[]}""").RootElement;
        Assert.Equal("（工具返回空内容）", McpProtocol.AggregateContent(empty, out _));
    }

    [Fact]
    public void ResolveLaunch_FindsExecutableAcrossExtensions()
    {
        // 用本机一定存在的命令做校验（dotnet / cmd）
        var found = StdioTransport.FindExecutable(OperatingSystem.IsWindows() ? "cmd" : "sh");
        Assert.NotNull(found);
    }
}

// ─────────────────────────── 客户端 / 管理器测试 ───────────────────────────

public class McpClientTests
{
    private static McpServerEntry Entry(string id = "demo") => new()
    {
        Id = id,
        Name = "Demo 服务器",
        Transport = "stdio",
        Command = "fake",
    };

    [Fact]
    public async Task Connect_HandshakeThenListTools_ReachesReady()
    {
        var transport = new LoopbackTransport();
        await using var client = new McpClient(Entry(), _ => transport);

        var connecting = client.ConnectAsync(CancellationToken.None);
        await transport.Server.HandleHandshakeAsync(
            """{"name":"echo","description":"回显工具","inputSchema":{"type":"object","properties":{"text":{"type":"string"}}}}""",
            """{"name":"add","description":"加法","inputSchema":{"type":"object","properties":{}}}""");
        await connecting;

        Assert.Equal(McpClientState.Ready, client.State);
        Assert.Equal("fake-server", client.Server?.Name);
        Assert.Equal("2024-11-05", client.ProtocolVersion);
        Assert.Equal(2, client.Tools.Count);
    }

    [Fact]
    public async Task CallTool_AggregatesContent()
    {
        var transport = new LoopbackTransport();
        await using var client = new McpClient(Entry(), _ => transport);
        var connecting = client.ConnectAsync(CancellationToken.None);
        await transport.Server.HandleHandshakeAsync("""{"name":"echo","inputSchema":{"type":"object"}}""");
        await connecting;

        var calling = client.CallToolAsync("echo", """{"text":"你好"}""");
        var request = await transport.Server.ReadRequestAsync();
        Assert.Contains("tools/call", request);
        Assert.Contains("\"echo\"", request);
        transport.Server.RespondTo(request, """{"content":[{"type":"text","text":"收到：你好"}],"isError":false}""");

        var result = await calling;
        Assert.Equal("收到：你好", result);
    }

    [Fact]
    public async Task CallTool_ServerError_ReturnsErrorText()
    {
        var transport = new LoopbackTransport();
        await using var client = new McpClient(Entry(), _ => transport);
        var connecting = client.ConnectAsync(CancellationToken.None);
        await transport.Server.HandleHandshakeAsync("""{"name":"boom","inputSchema":{"type":"object"}}""");
        await connecting;

        var calling = client.CallToolAsync("boom", "{}");
        var request = await transport.Server.ReadRequestAsync();
        using var doc = JsonDocument.Parse(request);
        var id = doc.RootElement.GetProperty("id").GetRawText();
        transport.Server.Respond("{\"jsonrpc\":\"2.0\",\"id\":" + id +
            ",\"error\":{\"code\":-32603,\"message\":\"内部错误\"}}");

        var result = await calling;
        Assert.Contains("错误", result);
        Assert.Contains("内部错误", result);
    }

    [Fact]
    public async Task CallTool_WhenNotReady_ReturnsFriendlyError()
    {
        await using var client = new McpClient(Entry(), _ => new LoopbackTransport());
        var result = await client.CallToolAsync("anything", "{}", CancellationToken.None);
        Assert.Contains("未就绪", result);
    }

    [Fact]
    public async Task Manager_AggregatesToolsAndRoutesCall()
    {
        var transport = new LoopbackTransport();
        await using var manager = new McpManager(_ => transport);
        var syncing = manager.SyncAsync([Entry("demo")]);
        await transport.Server.HandleHandshakeAsync(
            """{"name":"echo","description":"回显","inputSchema":{"type":"object","properties":{"text":{"type":"string"}}}}""");
        await syncing;

        var tools = manager.GetToolDefinitions();
        Assert.Single(tools);
        Assert.Equal("mcp__demo__echo", tools[0].Name);
        Assert.Contains("Demo 服务器", tools[0].Description);

        var status = manager.GetStatus();
        Assert.Single(status);
        Assert.True(status[0].Ready);
        Assert.Equal(1, status[0].ToolCount);

        var executing = manager.ExecuteToolAsync("mcp__demo__echo", """{"text":"hi"}""", CancellationToken.None);
        var request = await transport.Server.ReadRequestAsync();
        transport.Server.RespondTo(request, """{"content":[{"type":"text","text":"hi"}],"isError":false}""");
        Assert.Equal("hi", await executing);
    }

    [Fact]
    public async Task Manager_UnknownServer_RoutesToError()
    {
        await using var manager = new McpManager(_ => new LoopbackTransport());
        var result = await manager.ExecuteToolAsync("mcp__nope__echo", "{}", CancellationToken.None);
        Assert.Contains("不存在", result);
    }

    [Fact]
    public async Task Manager_DisabledServer_NotStarted()
    {
        var transport = new LoopbackTransport();
        await using var manager = new McpManager(_ => transport);
        var entry = Entry("off");
        entry.Enabled = false;
        await manager.SyncAsync([entry]);
        Assert.Empty(manager.GetToolDefinitions());
        Assert.Empty(manager.GetStatus());
    }
}

// ─────────────────────────── 内置目录测试 ───────────────────────────

public class McpCatalogTests
{
    [Fact]
    public void Builtin_HasEntriesWithUniqueNamesAndCommands()
    {
        var catalog = McpCatalog.Builtin;
        Assert.True(catalog.Count >= 5);
        Assert.Equal(catalog.Count, catalog.Select(c => c.Name).Distinct().Count());
        Assert.All(catalog, c =>
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Name));
            Assert.False(string.IsNullOrWhiteSpace(c.Command));
            Assert.Contains(c.Transport, new[] { "stdio", "http" });
        });
    }
}
