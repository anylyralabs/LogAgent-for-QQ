using LogAgent.Shared.Messaging;

namespace LogAgent.Core;

/// <summary>由组合根传入"回复用连接"（避免 Core 直接管理连接生命周期）。</summary>
public interface IBotConnectionFactory
{
    Task SendTextAsync(string chatId, ChatType chatType, string text, string? replyToMessageId, CancellationToken ct = default);

    /// <summary>
    /// 合并转发发送（OneBot/NapCat 支持；一个转发卡片承载多个分节）。
    /// 不支持转发的通道（官方 Bot / 本机对话）返回 false，由调用方回退普通发送。
    /// 默认实现 = 不支持。
    /// </summary>
    Task<bool> TrySendForwardAsync(string chatId, ChatType chatType, IReadOnlyList<string> sections, CancellationToken ct = default)
        => Task.FromResult(false);
}
