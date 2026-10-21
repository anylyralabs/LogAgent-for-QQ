using LogAgent.Shared.Messaging;

namespace LogAgent.Core;

/// <summary>
/// 由组合根传入"回复用连接"（避免 Core 直接管理连接生命周期）。
/// <para>发送能力已下沉到 <see cref="IMessageSender"/>：频道管理器与旧连接桥都实现它，
/// <see cref="AgentRuntime"/> 只依赖发送接口，不再关心通道来自哪一种协议。</para>
/// </summary>
public interface IBotConnectionFactory : IMessageSender
{
}
