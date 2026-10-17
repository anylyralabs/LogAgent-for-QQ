using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LogAgent.Shared.Configuration;

namespace LogAgent.Mcp;

/// <summary>
/// 单个 MCP 服务器连接（协议客户端）：
/// 启动传输 → initialize 握手（版本协商 + 服务器身份）→ notifications/initialized →
/// tools/list 拉取工具清单 → tools/call 执行工具。读循环负责把响应与请求配对。
/// </summary>
public sealed class McpClient : IAsyncDisposable
{
    private readonly McpServerEntry _config;
    private readonly Func<McpServerEntry, IMcpTransport> _transportFactory;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private IMcpTransport? _transport;
    private CancellationTokenSource? _loopCts;
    private Task? _readLoop;
    private long _nextId;

    public McpClient(McpServerEntry config, Func<McpServerEntry, IMcpTransport>? transportFactory = null)
    {
        _config = config;
        _transportFactory = transportFactory ?? (c => c.Transport == "http" ? new HttpTransport(c) : new StdioTransport(c));
        Fingerprint = BuildFingerprint(config);
    }

    public string Id => _config.Id;
    public string Name => string.IsNullOrEmpty(_config.Name) ? _config.Id : _config.Name;
    public McpClientState State { get; private set; } = McpClientState.Disconnected;
    public McpServerIdentity? Server { get; private set; }
    public string? ProtocolVersion { get; private set; }
    public string? Instructions { get; private set; }
    public IReadOnlyList<McpToolInfo> Tools { get; private set; } = [];
    public string? LastError { get; private set; }

    /// <summary>配置指纹（McpManager 判断是否需要重启连接）。</summary>
    public string Fingerprint { get; }

    public event EventHandler<string>? Log;
    public event EventHandler<string>? ServerStderr;

    /// <summary>连接流程：启动传输 → 握手 → 列举工具。失败不抛出（状态置 Failed）。</summary>
    public async Task ConnectAsync(CancellationToken ct)
    {
        State = McpClientState.Connecting;
        LastError = null;
        try
        {
            _transport = _transportFactory(_config);
            if (_transport is StdioTransport stdio)
                stdio.StderrLine += (_, line) => ServerStderr?.Invoke(this, line);

            await _transport.StartAsync(ct);
            _loopCts = new CancellationTokenSource();
            var token = _loopCts.Token;
            _readLoop = Task.Run(() => ReadLoopAsync(token), CancellationToken.None);

            // 1. initialize 握手（协议版本协商）
            var initResult = await SendRequestAsync(McpProtocol.MethodInitialize, new
            {
                protocolVersion = McpProtocol.ProtocolVersion,
                capabilities = new { },
                clientInfo = new { name = "LogAgent", version = "0.7.0" },
            }, TimeSpan.FromSeconds(60), ct);
            ProtocolVersion = initResult.TryGetProperty("protocolVersion", out var pv) ? pv.GetString() : null;
            if (initResult.TryGetProperty("serverInfo", out var si))
            {
                Server = new McpServerIdentity(
                    si.TryGetProperty("name", out var sn) ? sn.GetString() ?? "?" : "?",
                    si.TryGetProperty("version", out var sv) ? sv.GetString() ?? "?" : "?");
            }
            Instructions = initResult.TryGetProperty("instructions", out var ins) && ins.ValueKind == JsonValueKind.String
                ? ins.GetString()
                : null;

            // 2. 完成握手通知
            await WriteRawAsync(McpProtocol.BuildNotification(McpProtocol.MethodInitialized, null), ct);

            // 3. 拉取工具清单
            var toolsResult = await SendRequestAsync(McpProtocol.MethodListTools, new { }, TimeSpan.FromSeconds(30), ct);
            Tools = McpProtocol.ParseTools(toolsResult);

            State = McpClientState.Ready;
            EmitLog($"「{Name}」已连接：{Server?.Name} v{Server?.Version}（协议 {ProtocolVersion}），{Tools.Count} 个工具");
        }
        catch (Exception ex)
        {
            State = McpClientState.Failed;
            LastError = ex.Message;
            EmitLog($"「{Name}」连接失败：{ex.Message}");
            await CloseTransportAsync();
        }
    }

    /// <summary>执行工具调用，返回聚合后的文本（失败返回"错误：…"文本）。</summary>
    public async Task<string> CallToolAsync(string toolName, string argumentsJson, CancellationToken ct = default)
    {
        if (State != McpClientState.Ready)
            return $"错误：MCP 服务器「{Name}」未就绪（{LastError ?? "未连接"}）";

        JsonNode? arguments = new JsonObject();
        if (!string.IsNullOrWhiteSpace(argumentsJson))
        {
            try
            {
                arguments = JsonNode.Parse(argumentsJson);
            }
            catch (JsonException ex)
            {
                return $"错误：工具参数不是合法 JSON（{ex.Message}）";
            }
        }

        try
        {
            var result = await SendRequestAsync(McpProtocol.MethodCallTool,
                new { name = toolName, arguments = arguments ?? new JsonObject() },
                TimeSpan.FromSeconds(180), ct);
            var text = McpProtocol.AggregateContent(result, out var isError);
            text = Truncate(text, 8000);
            EmitLog($"调用 {toolName} 完成（{text.Length} 字符）");
            return isError ? "错误：" + text : text;
        }
        catch (McpException ex)
        {
            return $"错误：MCP 工具 {toolName} 失败：{ex.Message}";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return $"错误：MCP 工具 {toolName} 执行超时";
        }
        catch (Exception ex)
        {
            return $"错误：MCP 工具 {toolName} 执行失败：{ex.Message}";
        }
    }

    // ─────────────────────────── 请求/响应 ───────────────────────────

    private async Task<JsonElement> SendRequestAsync(string method, object? @params, TimeSpan timeout, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await WriteRawAsync(McpProtocol.BuildRequest(id, method, @params), ct);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            using var reg = timeoutCts.Token.Register(() => tcs.TrySetException(
                new TimeoutException($"{method} 超时（{timeout.TotalSeconds:F0}s）")));
            return await tcs.Task;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task WriteRawAsync(string json, CancellationToken ct)
    {
        if (_transport is null) throw new InvalidOperationException("传输尚未建立");
        await _writeLock.WaitAsync(ct);
        try
        {
            await _transport.WriteLineAsync(json, ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _transport is not null)
            {
                string? line;
                try
                {
                    line = await _transport.ReadLineAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception)
                {
                    line = null;
                }
                if (line is null) break;

                line = line.Trim();
                if (line.Length == 0 || line[0] != '{') continue; // 非协议行（如 npx 提示）忽略

                try
                {
                    Dispatch(line);
                }
                catch (JsonException)
                {
                    EmitLog($"收到无法解析的消息（已忽略）：{Truncate(line, 120)}");
                }
            }
        }
        finally
        {
            foreach (var (_, tcs) in _pending)
                tcs.TrySetException(new IOException("MCP 服务器连接已关闭"));
            _pending.Clear();
            if (State == McpClientState.Ready)
            {
                State = McpClientState.Failed;
                LastError = "连接已断开";
                EmitLog($"「{Name}」连接已断开");
            }
        }
    }

    private void Dispatch(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;

        if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number)
        {
            var hasMethod = root.TryGetProperty("method", out var methodEl);
            if (!hasMethod)
            {
                // 响应：与请求配对
                var id = idEl.GetInt64();
                if (!_pending.TryGetValue(id, out var tcs)) return;
                if (root.TryGetProperty("error", out var err))
                {
                    var code = err.TryGetProperty("code", out var c) ? c.GetInt32() : 0;
                    var message = err.TryGetProperty("message", out var m) ? m.GetString() ?? "未知错误" : "未知错误";
                    tcs.TrySetException(new McpException($"MCP {code}：{message}"));
                }
                else if (root.TryGetProperty("result", out var result))
                {
                    tcs.TrySetResult(result.Clone());
                }
                else
                {
                    tcs.TrySetException(new McpException("响应缺少 result/error"));
                }
                return;
            }

            // 服务器 → 客户端的请求（如 roots/list、sampling）：当前不支持，按协议回错误
            var serverMethod = methodEl.GetString() ?? "";
            EmitLog($"服务器请求 {serverMethod}（客户端不支持，已回错误）");
            _ = WriteRawAsync(McpProtocol.BuildErrorResponse(idEl, -32601, $"LogAgent 客户端不支持 {serverMethod}"),
                CancellationToken.None);
            return;
        }

        // 通知（无 id）：记录即可
        if (root.TryGetProperty("method", out var method))
        {
            var name = method.GetString() ?? "";
            if (name == "notifications/message" && root.TryGetProperty("params", out var prm)
                && prm.TryGetProperty("data", out var data))
                EmitLog($"服务器日志：{Truncate(data.ToString(), 200)}");
        }
    }

    // ─────────────────────────── 关闭 ───────────────────────────

    public async ValueTask DisposeAsync()
    {
        await CloseTransportAsync();
    }

    private async Task CloseTransportAsync()
    {
        _loopCts?.Cancel();
        var transport = _transport;
        _transport = null;
        if (transport is not null)
        {
            try { await transport.DisposeAsync(); } catch (Exception) { /* 已关闭 */ }
        }
        if (_readLoop is not null)
        {
            try { await _readLoop.WaitAsync(TimeSpan.FromSeconds(3)); } catch (Exception) { /* 超时/异常忽略 */ }
        }
    }

    private static string BuildFingerprint(McpServerEntry c)
    {
        var env = string.Join(";", c.Env.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));
        var headers = string.Join(";", c.Headers.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));
        return $"{c.Transport}|{c.Command}|{c.Args}|{c.WorkingDirectory}|{c.Url}|{env}|{headers}";
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    private void EmitLog(string line) => Log?.Invoke(this, line);
}
