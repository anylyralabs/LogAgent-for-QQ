using LogAgent.Shared.Chat;

namespace LogAgent.Routing;

/// <summary>一次补全请求（Tools 非空时允许模型发起工具调用）。</summary>
public sealed record ChatRequest(
    IReadOnlyList<ChatTurn> Messages,
    int MaxTokens = 4096,
    double Temperature = 0.7,
    IReadOnlyList<ChatToolDefinition>? Tools = null);

/// <summary>补全结果 + 用量计量（FinishReason 为 "tool_calls" 时 ToolCalls 非空）。</summary>
public sealed record ChatResult(
    string Content,
    string ProviderId,
    string Model,
    int PromptTokens,
    int CompletionTokens,
    TimeSpan Latency,
    IReadOnlyList<ChatToolCall>? ToolCalls = null,
    string? FinishReason = null);

/// <summary>模型客户端抽象——实现方对接具体 provider（OpenAI 兼容 HTTP / 未来其它协议）。</summary>
public interface IChatModelClient
{
    string ProviderId { get; }

    /// <summary>执行一次补全。失败抛异常，由路由器决定切换。</summary>
    Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken ct = default);
}
