using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace LogAgent.Mcp;

/// <summary>
/// MCP 协议常量与消息工具（Model Context Protocol，JSON-RPC 2.0 over stdio / Streamable HTTP）。
/// 参考规范版本 2024-11-05（工具能力部分）。
/// </summary>
public static class McpProtocol
{
    /// <summary>客户端声明的协议版本。</summary>
    public const string ProtocolVersion = "2024-11-05";

    public const string MethodInitialize = "initialize";
    public const string MethodInitialized = "notifications/initialized";
    public const string MethodListTools = "tools/list";
    public const string MethodCallTool = "tools/call";

    /// <summary>MCP 工具的限定名前缀（serverId 部分只保留 [A-Za-z0-9_]）。</summary>
    public const string ToolPrefix = "mcp__";

    public static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>把服务器 id 清洗为工具名安全片段（非字母数字 → 下划线）。</summary>
    public static string SanitizeId(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw)
            sb.Append(char.IsAsciiLetterOrDigit(c) ? c : '_');
        return sb.Length == 0 ? "_" : sb.ToString();
    }

    /// <summary>生成模型可见的限定工具名：mcp__{serverId}__{toolName}。</summary>
    public static string QualifyToolName(string serverId, string toolName)
        => $"{ToolPrefix}{SanitizeId(serverId)}__{SanitizeId(toolName)}";

    /// <summary>拆分限定工具名；非 MCP 前缀返回 null。</summary>
    public static (string ServerId, string ToolName)? SplitQualified(string qualifiedName)
    {
        if (!qualifiedName.StartsWith(ToolPrefix, StringComparison.Ordinal)) return null;
        var rest = qualifiedName[ToolPrefix.Length..];
        var sep = rest.IndexOf("__", StringComparison.Ordinal);
        if (sep <= 0 || sep >= rest.Length - 2) return null;
        return (rest[..sep], rest[(sep + 2)..]);
    }

    /// <summary>构建 JSON-RPC 请求（单行）。</summary>
    public static string BuildRequest(long id, string method, object? @params)
    {
        var obj = new System.Text.Json.Nodes.JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
        };
        if (@params is not null)
            obj["params"] = JsonSerializer.SerializeToNode(@params, Json);
        return obj.ToJsonString(Json);
    }

    /// <summary>构建 JSON-RPC 通知（无 id）。</summary>
    public static string BuildNotification(string method, object? @params)
    {
        var obj = new System.Text.Json.Nodes.JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
        };
        if (@params is not null)
            obj["params"] = JsonSerializer.SerializeToNode(@params, Json);
        return obj.ToJsonString(Json);
    }

    /// <summary>构建 JSON-RPC 错误响应（回应服务器→客户端的不支持请求）。</summary>
    public static string BuildErrorResponse(JsonElement id, int code, string message)
    {
        var obj = new System.Text.Json.Nodes.JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = System.Text.Json.Nodes.JsonNode.Parse(id.GetRawText()),
            ["error"] = new System.Text.Json.Nodes.JsonObject
            {
                ["code"] = code,
                ["message"] = message,
            },
        };
        return obj.ToJsonString(Json);
    }

    /// <summary>解析 tools/list 响应中的工具列表。</summary>
    public static List<McpToolInfo> ParseTools(JsonElement result)
    {
        var tools = new List<McpToolInfo>();
        if (!result.TryGetProperty("tools", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return tools;
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (string.IsNullOrWhiteSpace(name)) continue;
            var description = item.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString() ?? ""
                : "";
            var schema = item.TryGetProperty("inputSchema", out var s) && s.ValueKind == JsonValueKind.Object
                ? s.GetRawText()
                : """{"type":"object","properties":{}}""";
            tools.Add(new McpToolInfo(name, description, schema));
        }
        return tools;
    }

    /// <summary>聚合 tools/call 的 content 数组为纯文本（text 取原文，其他类型给占位说明）。</summary>
    public static string AggregateContent(JsonElement result, out bool isError)
    {
        isError = result.TryGetProperty("isError", out var e) && e.ValueKind == JsonValueKind.True;
        var sb = new StringBuilder();
        if (result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in content.EnumerateArray())
            {
                var type = item.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                switch (type)
                {
                    case "text":
                        sb.AppendLine(item.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "");
                        break;
                    case "image":
                        sb.AppendLine("[图片内容（当前不支持展示）]");
                        break;
                    case "resource":
                        var uri = item.TryGetProperty("resource", out var r)
                            && r.TryGetProperty("uri", out var u) ? u.GetString() : "";
                        sb.AppendLine($"[资源引用：{uri}]");
                        break;
                    default:
                        sb.AppendLine($"[{type}]");
                        break;
                }
            }
        }
        var text = sb.ToString().Trim();
        return text.Length == 0 ? "（工具返回空内容）" : text;
    }
}

/// <summary>MCP 服务器暴露的一个工具。</summary>
public sealed record McpToolInfo(string Name, string Description, string InputSchemaJson);

/// <summary>MCP 服务器自报身份（initialize 响应）。</summary>
public sealed record McpServerIdentity(string Name, string Version);

/// <summary>MCP 服务连接状态。</summary>
public enum McpClientState
{
    Disconnected,
    Connecting,
    Ready,
    Failed,
}

/// <summary>MCP 协议级错误（服务器返回的 JSON-RPC error）。</summary>
public sealed class McpException(string message) : Exception(message);
