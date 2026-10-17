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
}
