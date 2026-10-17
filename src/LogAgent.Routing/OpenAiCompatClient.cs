using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using LogAgent.Shared.Chat;
using LogAgent.Shared.Configuration;

namespace LogAgent.Routing;

/// <summary>OpenAI 兼容 /chat/completions 客户端（对接任意兼容 provider）。</summary>
public sealed class OpenAiCompatClient(ProviderConfig config, HttpClient? httpClient = null) : IChatModelClient
{
    public string ProviderId => config.Id;

    private HttpClient Http { get; } = httpClient ?? CreateClient(config);

    /// <summary>序列化选项：中文原文输出（默认会转义为 \uXXXX，可读性差）。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static HttpClient CreateClient(ProviderConfig c)
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(c.TimeoutSeconds) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", c.ApiKey);
        return client;
    }

    public async Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken ct = default)
    {
        var started = DateTimeOffset.UtcNow;
        var payload = new Dictionary<string, object?>
        {
            ["model"] = config.ChatModel,
            ["messages"] = request.Messages.Select(BuildMessage).ToList(),
            ["max_tokens"] = request.MaxTokens,
            ["temperature"] = request.Temperature,
        };
        if (request.Tools is { Count: > 0 })
        {
            payload["tools"] = request.Tools.Select(t => (object)new
            {
                type = "function",
                function = new
                {
                    name = t.Name,
                    description = t.Description,
                    parameters = ParseJsonElement(t.ParametersJson),
                },
            }).ToList();
        }

        var url = $"{config.Endpoint.TrimEnd('/')}/chat/completions";
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        using var httpContent = new StringContent(json, Encoding.UTF8, "application/json");
        using var resp = await Http.PostAsync(url, httpContent, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var choice = root.GetProperty("choices")[0];
        var message = choice.GetProperty("message");
        var content = message.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String
            ? contentEl.GetString() ?? ""
            : "";
        var toolCalls = ParseToolCalls(message);
        var finishReason = choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String
            ? fr.GetString()
            : null;
        var usage = root.TryGetProperty("usage", out var u)
            ? u
            : (JsonElement?)null;
        var promptTokens = usage?.TryGetProperty("prompt_tokens", out var pt) == true ? pt.GetInt32() : 0;
        var completionTokens = usage?.TryGetProperty("completion_tokens", out var ct2) == true ? ct2.GetInt32() : 0;
        return new ChatResult(
            content,
            config.Id,
            config.ChatModel,
            promptTokens,
            completionTokens,
            DateTimeOffset.UtcNow - started,
            toolCalls.Count > 0 ? toolCalls : null,
            finishReason);
    }

    /// <summary>消息序列化：普通消息 / 工具回执（role=tool）/ 带工具调用的助手消息 / 多模态（图片）。</summary>
    private static object BuildMessage(ChatTurn m)
    {
        if (m.ToolCalls is { Count: > 0 })
        {
            return new
            {
                role = "assistant",
                content = string.IsNullOrEmpty(m.Content) ? null : m.Content,
                tool_calls = m.ToolCalls.Select(tc => new
                {
                    id = tc.Id,
                    type = "function",
                    function = new { name = tc.Name, arguments = tc.Arguments },
                }).ToList(),
            };
        }
        if (m.ToolCallId is { } toolCallId)
            return new { role = "tool", content = m.Content, tool_call_id = toolCallId };
        if (m.ImageDataUrls is { Count: > 0 })
        {
            // 多模态：content 数组（text part + image_url part），OpenAI 兼容格式
            var parts = new List<object>();
            if (!string.IsNullOrEmpty(m.Content))
                parts.Add(new { type = "text", text = m.Content });
            foreach (var dataUri in m.ImageDataUrls)
                parts.Add(new { type = "image_url", image_url = new { url = dataUri } });
            return new { role = m.Role, content = parts };
        }
        return new { role = m.Role, content = m.Content };
    }

    /// <summary>解析响应中的工具调用列表（容错：条目异常跳过）。</summary>
    private static List<ChatToolCall> ParseToolCalls(JsonElement message)
    {
        var calls = new List<ChatToolCall>();
        if (!message.TryGetProperty("tool_calls", out var toolCallsEl) || toolCallsEl.ValueKind != JsonValueKind.Array)
            return calls;
        foreach (var tc in toolCallsEl.EnumerateArray())
        {
            if (!tc.TryGetProperty("function", out var fn)) continue;
            var name = fn.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            if (string.IsNullOrEmpty(name)) continue;
            var id = tc.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() ?? "" : "";
            var args = fn.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.String
                ? a.GetString() ?? "{}"
                : "{}";
            calls.Add(new ChatToolCall(id, name, args));
        }
        return calls;
    }

    private static JsonElement ParseJsonElement(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// <summary>拉取接口可用模型列表（GET {Endpoint}/models，OpenAI 兼容；只返回 id）。</summary>
    public async Task<List<string>> ListModelsAsync(CancellationToken ct = default)
        => ParseModels(await FetchModelsJsonAsync(ct)).Select(m => m.Id).ToList();

    /// <summary>拉取模型列表（含元数据：上下文长度 / 最大输出 / 思考标志；接口未提供时为零值）。</summary>
    public async Task<List<ModelInfo>> ListModelInfosAsync(CancellationToken ct = default)
        => ParseModels(await FetchModelsJsonAsync(ct));

    private async Task<string> FetchModelsJsonAsync(CancellationToken ct)
    {
        var url = $"{config.Endpoint.TrimEnd('/')}/models";
        using var resp = await Http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    /// <summary>模型信息（/models 条目 + 可选能力元数据）。</summary>
    public sealed record ModelInfo(
        string Id, string? Name = null, int ContextTokens = 0, int MaxOutputTokens = 0, bool Reasoning = false);

    /// <summary>解析 /models 响应：data[] → ModelInfo（容错：data 缺失/条目异常均跳过），去重排序。</summary>
    public static List<ModelInfo> ParseModels(string json)
    {
        var models = new List<ModelInfo>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) continue;
                var value = id.GetString();
                if (string.IsNullOrWhiteSpace(value)) continue;
                var name = item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                    ? n.GetString()
                    : null;
                var reasoning =
                    item.TryGetProperty("supports_reasoning", out var sr) && sr.ValueKind == JsonValueKind.True
                    || item.TryGetProperty("only_reasoning", out var or) && or.ValueKind == JsonValueKind.True;
                models.Add(new ModelInfo(
                    value, name,
                    GetInt(item, "context_length"),
                    GetInt(item, "max_output_tokens"),
                    reasoning));
            }
        }
        return models
            .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>解析 /models 响应：data[].id（兼容旧调用），去重排序。</summary>
    public static List<string> ParseModelIds(string json) => ParseModels(json).Select(m => m.Id).ToList();

    private static int GetInt(JsonElement item, string name)
        => item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)
            ? n
            : 0;
}
