using System.Collections.Concurrent;
using LogAgent.Shared.Chat;
using LogAgent.Shared.Configuration;

namespace LogAgent.Mcp;

/// <summary>MCP 服务器运行状态（UI 展示用）。</summary>
public sealed record McpServerStatus(
    string Id, string Name, string Transport, string StateText, bool Ready, int ToolCount, string? Error);

/// <summary>
/// MCP 管理器：按配置同步多个服务器连接（新增启动 / 移除停止 / 变更重启），
/// 聚合所有服务器工具为模型可见的工具定义，并按限定名路由调用。
/// </summary>
public sealed class McpManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, McpClient> _clients = new();
    private readonly Func<McpServerEntry, IMcpTransport>? _transportFactory;
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    public event EventHandler<string>? Log;

    public McpManager(Func<McpServerEntry, IMcpTransport>? transportFactory = null)
    {
        _transportFactory = transportFactory;
    }

    /// <summary>按配置同步连接（并发启动新服务器；阻塞至全部就绪或失败）。</summary>
    public async Task SyncAsync(IReadOnlyList<McpServerEntry> servers, CancellationToken ct = default)
    {
        await _syncLock.WaitAsync(ct);
        try
        {
            var wanted = servers
                .Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Id) && !string.IsNullOrWhiteSpace(s.Name))
                .GroupBy(s => s.Id)
                .ToDictionary(g => g.Key, g => g.Last());

            // 停止：移除的 / 禁用的 / 配置变更的
            foreach (var (id, client) in _clients.ToList())
            {
                if (wanted.TryGetValue(id, out var entry) && client.Fingerprint == FingerprintOf(entry))
                    continue;
                _clients.TryRemove(id, out _);
                await client.DisposeAsync();
                Log?.Invoke(this, $"已停止：{id}");
            }

            // 启动：新增的
            var starting = new List<Task>();
            foreach (var (id, entry) in wanted)
            {
                if (_clients.ContainsKey(id)) continue;
                var client = new McpClient(entry, _transportFactory);
                client.Log += (_, line) => Log?.Invoke(this, line);
                client.ServerStderr += (_, line) => Log?.Invoke(this, $"「{client.Name}」stderr：{line}");
                _clients[id] = client;
                starting.Add(Task.Run(() => client.ConnectAsync(ct), ct));
            }
            if (starting.Count > 0)
                await Task.WhenAll(starting);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    /// <summary>全部就绪服务器的工具定义（模型可见，限定名 mcp__{server}__{tool}）。</summary>
    public IReadOnlyList<ChatToolDefinition> GetToolDefinitions()
    {
        var list = new List<ChatToolDefinition>();
        foreach (var client in _clients.Values)
        {
            if (client.State != McpClientState.Ready) continue;
            foreach (var tool in client.Tools)
            {
                var description = string.IsNullOrWhiteSpace(tool.Description)
                    ? $"（MCP 工具，来自「{client.Name}」）"
                    : tool.Description;
                list.Add(new ChatToolDefinition(
                    McpProtocol.QualifyToolName(client.Id, tool.Name),
                    $"[MCP·{client.Name}] {description}",
                    tool.InputSchemaJson));
            }
        }
        return list;
    }

    /// <summary>按限定名路由工具调用。</summary>
    public async Task<string> ExecuteToolAsync(string qualifiedName, string argumentsJson, CancellationToken ct)
    {
        var split = McpProtocol.SplitQualified(qualifiedName);
        if (split is null) return $"错误：不是合法的 MCP 工具名（{qualifiedName}）";
        var (serverId, toolName) = split.Value;
        if (!_clients.TryGetValue(serverId, out var client))
            return $"错误：MCP 服务器 {serverId} 不存在（可能已被移除或禁用）";
        return await client.CallToolAsync(toolName, argumentsJson, ct);
    }

    /// <summary>运行状态快照（UI 展示）。</summary>
    public IReadOnlyList<McpServerStatus> GetStatus() =>
        _clients.Values
            .OrderBy(c => c.Id, StringComparer.Ordinal)
            .Select(c => new McpServerStatus(
                c.Id, c.Name,
                c.Fingerprint.StartsWith("http|", StringComparison.Ordinal) ? "HTTP" : "stdio",
                c.State switch
                {
                    McpClientState.Ready => "已连接",
                    McpClientState.Connecting => "连接中",
                    McpClientState.Failed => "失败",
                    _ => "未连接",
                },
                c.State == McpClientState.Ready,
                c.Tools.Count,
                c.LastError))
            .ToList();

    private static string FingerprintOf(McpServerEntry c)
    {
        var env = string.Join(";", c.Env.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));
        var headers = string.Join(";", c.Headers.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));
        return $"{c.Transport}|{c.Command}|{c.Args}|{c.WorkingDirectory}|{c.Url}|{env}|{headers}";
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (_, client) in _clients)
            await client.DisposeAsync();
        _clients.Clear();
    }
}
