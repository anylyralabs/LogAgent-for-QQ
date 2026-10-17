namespace LogAgent.Shared.Messaging;

public enum ChatType { Group, Private }

/// <summary>消息附件（QQ 文件等）：名称 + 可下载地址 + 大小；解析失败时携带原因（Url 为空）。</summary>
public sealed record MessageAttachment(string Name, string Url, long Size = 0)
{
    /// <summary>无法解析下载地址时的原因（供富化环节如实告知模型；正常附件为 null）。</summary>
    public string? UnavailableReason { get; init; }
}

/// <summary>统一消息模型——双协议（OneBot11 / QQ官方Bot）解析后的内部表示。</summary>
public sealed class BotMessage
{
    public required string MessageId { get; init; }
    public required string ChatId { get; init; }
    public required ChatType ChatType { get; init; }
    public required string SenderId { get; init; }
    public string SenderName { get; init; } = "";
    public required string Text { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public bool MentionedMe { get; init; }
    public string? RawJson { get; init; }

    /// <summary>附件列表（QQ 文件；由连接层解析并解析出下载地址，内容由消息富化环节读取）。</summary>
    public IReadOnlyList<MessageAttachment>? Attachments { get; init; }

    /// <summary>图片 URL 列表（QQ 图片消息；由 Agent 组装时下载转多模态供模型读图）。</summary>
    public IReadOnlyList<string>? ImageUrls { get; init; }
}

public sealed class MessageReceivedEventArgs(BotMessage message) : EventArgs
{
    public BotMessage Message { get; } = message;
}
