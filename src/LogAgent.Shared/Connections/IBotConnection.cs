namespace LogAgent.Shared.Connections;

using LogAgent.Shared.Messaging;

/// <summary>协议连接抽象：OneBot11 与 QQ 官方机器人适配器共同实现（规划 3.1）。</summary>
public interface IBotConnection : IAsyncDisposable
{
    string Name { get; }
    bool IsConnected { get; }

    event EventHandler<MessageReceivedEventArgs>? MessageReceived;
    event EventHandler<string>? ConnectionLog;

    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);

    /// <summary>发送文本消息到指定会话。官方 Bot 适配器需带 replyToMessageId 做被动回复。</summary>
    Task SendTextAsync(string chatId, ChatType chatType, string text, string? replyToMessageId = null, CancellationToken ct = default);

    /// <summary>主动发送（不带 msg_id）。群聊主动消息需群主在 QQ 中开启「机器人主动在群聊内发言」。</summary>
    Task SendProactiveAsync(string chatId, ChatType chatType, string text, CancellationToken ct = default)
        => SendTextAsync(chatId, chatType, text, null, ct);

    /// <summary>
    /// 合并发送：OneBot 发合并转发卡片；官方 Bot 合并为一条 Markdown/文本消息。
    /// 协议不支持时返回 false，由回复策略回退普通发送。默认实现 = 不支持。
    /// </summary>
    Task<bool> TrySendForwardAsync(string chatId, ChatType chatType, IReadOnlyList<string> sections,
        string? replyToMessageId = null, CancellationToken ct = default)
        => Task.FromResult(false);
}
