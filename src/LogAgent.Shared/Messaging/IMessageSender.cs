namespace LogAgent.Shared.Messaging;

/// <summary>
/// 回复发送抽象：Agent 运行时只依赖「往某个会话发消息」，不关心底层是 OneBot11、
/// 官方 Bot 还是本机对话。放在 Shared 里，连接层（ChannelManager）与 Core 都能复用，
/// 避免为了一个发送接口把协议实现拉进 Core。
/// </summary>
public interface IMessageSender
{
    Task SendTextAsync(string chatId, ChatType chatType, string text, string? replyToMessageId, CancellationToken ct = default);

    /// <summary>
    /// 主动发送（不带 replyToMessageId）。官方 Bot 群聊要求群主开启「机器人主动在群聊内发言」，
    /// 单聊则受平台主动消息频控限制；失败由具体实现记录日志。
    /// </summary>
    Task SendProactiveAsync(string chatId, ChatType chatType, string text, CancellationToken ct = default)
        => SendTextAsync(chatId, chatType, text, null, ct);

    /// <summary>
    /// 合并发送：OneBot 发合并转发卡片；官方 Bot 把多个分节合并成一条 Markdown/文本消息
    /// （规避 5 次被动回复上限）。协议既不支持转发也不支持合并时返回 false，由调用方回退普通发送。
    /// </summary>
    Task<bool> TrySendForwardAsync(string chatId, ChatType chatType, IReadOnlyList<string> sections,
        string? replyToMessageId = null, CancellationToken ct = default)
        => Task.FromResult(false);
}
