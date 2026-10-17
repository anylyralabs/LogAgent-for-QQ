namespace LogAgent.Shared.Chat;

/// <summary>模型请求调用的工具（函数名 + JSON 参数字符串 + 回执 Id）。</summary>
public sealed record ChatToolCall(string Id, string Name, string Arguments);

/// <summary>暴露给模型的工具定义（名称 + 说明 + JSON Schema 参数字符串）。</summary>
public sealed record ChatToolDefinition(string Name, string Description, string ParametersJson);

/// <summary>一次对话轮次中的单条消息（角色 + 内容；支持工具调用、工具回执与多模态图片）。</summary>
public sealed record ChatTurn(string Role, string Content, string? ToolCallId = null,
    IReadOnlyList<ChatToolCall>? ToolCalls = null, IReadOnlyList<string>? ImageDataUrls = null)
{
    public static ChatTurn System(string content) => new("system", content);
    public static ChatTurn User(string content) => new("user", content);
    public static ChatTurn Assistant(string content) => new("assistant", content);

    /// <summary>工具执行结果回执（role=tool，携带对应调用的 Id）。</summary>
    public static ChatTurn Tool(string toolCallId, string content) => new("tool", content, ToolCallId: toolCallId);

    /// <summary>带工具调用的助手消息（模型发起；content 可为空）。</summary>
    public static ChatTurn AssistantWithTools(string? content, IReadOnlyList<ChatToolCall> calls)
        => new("assistant", content ?? "", ToolCalls: calls);

    /// <summary>带图片的用户消息（ImageDataUrls 为 data:image/...;base64,... 列表）。</summary>
    public static ChatTurn UserWithImages(string content, IReadOnlyList<string> imageDataUrls)
        => new("user", content, ImageDataUrls: imageDataUrls);
}
