using System.Collections.Concurrent;
using LogAgent.Shared.Configuration;
using LogAgent.Shared.Connections;
using LogAgent.Shared.Messaging;

namespace LogAgent.Connections;

/// <summary>频道运行状态快照（UI / doctor / 日志用）。</summary>
public sealed record ChannelStatus(
    string Id,
    string Name,
    string Transport,
    int Priority,
    bool Enabled,
    bool Running,
    bool Connected,
    bool Pending,
    string? Error,
    ChannelCapabilities Capabilities)
{
    /// <summary>可直接展示的一行状态。</summary>
    public string StateText => Error is not null ? $"启动失败：{Error}"
        : Connected ? "已连接"
        : Running ? "连接中…"
        : !Enabled ? "未启用"
        : "未启动";
}

/// <summary>频道收到消息：带频道 ID，供宿主把回复发回原频道。</summary>
public sealed class ChannelMessageEventArgs(string channelId, BotMessage message, int mergedCount = 1) : EventArgs
{
    public string ChannelId { get; } = channelId;
    public BotMessage Message { get; } = message;

    /// <summary>这条消息由几条原始消息合并而成（1 = 未合并）。</summary>
    public int MergedCount { get; } = mergedCount;
}

/// <summary>一个已装配的频道（配置 + 协议连接）。</summary>
public sealed class ChannelRuntime(ChannelConfig config, int order)
{
    private IBotConnection? _connection;
    private string? _lastError;

    public ChannelConfig Config { get; } = config;
    internal int Order { get; } = order;

    public string Id => Config.Id;
    public string Name => Config.Name;

    public IBotConnection? Connection => _connection;

    /// <summary>已成功调用过 Start（不代表协议已连上）。</summary>
    public bool Running => _connection is not null;

    /// <summary>协议层已连接（NapCat 已连入 / 官方 Bot WS 已握手）。</summary>
    public bool Connected => _connection?.IsConnected ?? false;

    public bool Pending => Running && !Connected;

    public string? LastError => _lastError;

    internal void Attach(IBotConnection connection)
    {
        _connection = connection;
        _lastError = null;
    }

    internal void Fail(string error)
    {
        _connection = null;
        _lastError = error;
    }

    internal void Detach()
    {
        _connection = null;
        _lastError = null;
    }

    public ChannelStatus Snapshot() =>
        new(Id, Name, Config.Transport, Config.Priority, Config.Enabled,
            Running, Connected, Pending, _lastError, Config.Capabilities);
}

/// <summary>
/// 频道管理器：按 OpenClaw 的 channel 语义，把「平台接入」当成可并列运行的频道集合。
///
/// <para>职责：
/// ① 按优先级启动所有启用的频道（官方 Bot 默认 100 → 先于 OneBot11 的 50）；
/// ② 单个频道启动失败不影响其他频道；
/// ③ 收到的消息带上频道 ID，宿主通过 <see cref="CreateSender"/> 从原频道回复，
///    不再出现「OneBot 与官方 Bot 都在线时，把官方 Bot 的消息从 NapCat 发出去」的问题；
/// ④ 提供状态快照，UI / doctor 无需分别读两种连接对象。</para>
/// </summary>
public sealed class ChannelManager : IAsyncDisposable
{
    private const int MaxMergeBatch = 10;
    private readonly List<ChannelRuntime> _channels = [];
    private readonly Func<ChannelConfig, IBotConnection> _factory;
    private readonly ConcurrentDictionary<string, MergeBuffer> _mergeBuffers = new();

    public ChannelManager(IEnumerable<ChannelConfig> channels, Func<ChannelConfig, IBotConnection>? factory = null)
    {
        _factory = factory ?? ChannelFactory.Create;
        var order = 0;
        foreach (var channel in channels)
        {
            if (channel is null || string.IsNullOrWhiteSpace(channel.Id)) continue;
            channel.OneBot11 ??= new OneBot11Config();
            channel.Capabilities ??= new ChannelCapabilities();
            _channels.Add(new ChannelRuntime(channel, order++));
        }
    }

    /// <summary>频道连接日志（已带频道名前缀）。</summary>
    public event EventHandler<string>? Log;

    /// <summary>任一频道收到消息（事件参数带频道 ID）。</summary>
    public event EventHandler<ChannelMessageEventArgs>? MessageReceived;

    /// <summary>全部频道（配置顺序）。</summary>
    public IReadOnlyList<ChannelRuntime> Channels => _channels;

    public ChannelRuntime? Find(string channelId) =>
        _channels.FirstOrDefault(c => string.Equals(c.Id, channelId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 按优先级启动所有启用频道。单频道失败只记录日志并继续，返回是否全部成功。
    /// </summary>
    public async Task<bool> StartAllAsync(CancellationToken ct = default)
    {
        var allOk = true;
        foreach (var runtime in _channels
                     .Where(c => c.Config.Enabled)
                     .OrderByDescending(c => c.Config.Priority)
                     .ThenBy(c => c.Order))
        {
            if (!await StartAsync(runtime.Id, ct)) allOk = false;
        }
        return allOk;
    }

    /// <summary>启动一个频道；已启动返回 true，失败记录 <see cref="ChannelRuntime.LastError"/> 并返回 false。</summary>
    public async Task<bool> StartAsync(string channelId, CancellationToken ct = default)
    {
        var runtime = Find(channelId);
        if (runtime is null)
        {
            RaiseLog($"[频道] 未找到 {channelId}");
            return false;
        }
        if (runtime.Running) return true;

        IBotConnection? connection = null;
        try
        {
            connection = _factory(runtime.Config);
            connection.ConnectionLog += (_, line) => RaiseLog(Prefix(runtime, line));
            connection.MessageReceived += (_, e) =>
            {
                try { HandleRawMessage(runtime, e.Message); }
                catch (Exception ex) { RaiseLog($"[频道:{runtime.Name}] 消息处理异常：{ex.Message}"); }
            };
            await connection.StartAsync(ct);
            runtime.Attach(connection);
            RaiseLog($"[频道:{runtime.Name}] 已启动（{ChannelTransport.DisplayName(runtime.Config.Transport)}，优先级 {runtime.Config.Priority}）");
            return true;
        }
        catch (Exception ex)
        {
            if (connection is not null)
            {
                try { await connection.DisposeAsync(); }
                catch (Exception disposeEx) { RaiseLog($"[频道:{runtime.Name}] 启动失败后的清理异常：{disposeEx.Message}"); }
            }
            runtime.Fail(ex.Message);
            RaiseLog($"[频道:{runtime.Name}] 启动失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>停止一个频道；未启动时静默返回。</summary>
    public async Task StopAsync(string channelId)
    {
        FlushChannels(channelId);
        var runtime = Find(channelId);
        if (runtime?.Connection is null) return;

        var connection = runtime.Connection;
        runtime.Detach();
        try
        {
            await connection.DisposeAsync();
            RaiseLog($"[频道:{runtime.Name}] 已停止");
        }
        catch (Exception ex)
        {
            runtime.Fail(ex.Message);
            RaiseLog($"[频道:{runtime.Name}] 停止异常：{ex.Message}");
        }
    }

    /// <summary>停止全部频道（退出路径尽力而为，不抛出）。</summary>
    public async Task StopAllAsync()
    {
        // 先把还在合并窗口里的消息放出去，避免退出/切频道时丢消息
        FlushChannels();
        foreach (var runtime in _channels.Where(c => c.Running).ToList())
        {
            try { await StopAsync(runtime.Id); }
            catch (Exception ex) { RaiseLog($"[频道:{runtime.Name}] 停止异常：{ex.Message}"); }
        }
    }

    /// <summary>
    /// 创建「从指定频道回复」的发送器。消息从哪个频道来，就必须从哪个频道回，
    /// 避免跨频道串线（会话 ID 在不同协议下可能互不相识）。
    /// </summary>
    public IMessageSender CreateSender(string channelId)
    {
        if (Find(channelId) is null)
            throw new InvalidOperationException($"未找到频道 {channelId}");
        return new ChannelSender(this, channelId);
    }

    /// <summary>
    /// 主动发送用的首选频道：已连接频道中优先级最高者（官方 Bot 优先）。
    /// 没有已连接频道时返回 null。
    /// </summary>
    public IMessageSender? PreferredSender
    {
        get
        {
            var runtime = _channels
                .Where(c => c.Connected)
                .OrderByDescending(c => c.Config.Priority)
                .ThenBy(c => c.Order)
                .FirstOrDefault();
            return runtime is null ? null : CreateSender(runtime.Id);
        }
    }

    /// <summary>状态快照（按优先级从高到低）。</summary>
    public IReadOnlyList<ChannelStatus> Snapshot() => _channels
        .OrderByDescending(c => c.Config.Priority)
        .ThenBy(c => c.Order)
        .Select(c => c.Snapshot())
        .ToList();

    public async ValueTask DisposeAsync() => await StopAllAsync();

    // ─────────────────────── 入站合并（OpenClaw inbound merge） ───────────────────────

    /// <summary>
    /// 官方 Bot 的群消息可能被用户快速拆成多条发送。把窗口内的连续群消息合并成一次 Agent 轮次：
    /// 前面的消息作为带发送者标注的上下文，最后一条作为「当前消息」。
    /// 命令消息（/ 开头或新话题）立即处理，绝不合并。
    /// </summary>
    private void HandleRawMessage(ChannelRuntime runtime, BotMessage message)
    {
        var config = runtime.Config;
        if (!config.MergeMessages || config.MergeWindowMs <= 0 ||
            message.ChatType != ChatType.Group || IsCommand(message.Text))
        {
            if (IsCommand(message.Text)) FlushChannels(runtime.Id);
            Emit(runtime, message, 1);
            return;
        }

        var key = MergeKey(runtime.Id, message.ChatId);
        var buffer = _mergeBuffers.GetOrAdd(key, _ => new MergeBuffer(runtime.Id));
        bool flushNow;
        lock (buffer.Gate)
        {
            buffer.Messages.Add(message);
            buffer.Generation++;
            buffer.Delay?.Cancel();
            buffer.Delay?.Dispose();
            buffer.Delay = null;
            flushNow = buffer.Messages.Count >= MaxMergeBatch;
            if (!flushNow)
            {
                var generation = buffer.Generation;
                var cts = new CancellationTokenSource();
                buffer.Delay = cts;
                _ = FlushAfterDelayAsync(key, runtime, buffer, generation, config.MergeWindowMs, cts.Token);
            }
        }

        if (flushNow) Flush(key, runtime, buffer);
    }

    private async Task FlushAfterDelayAsync(string key, ChannelRuntime runtime, MergeBuffer buffer,
        int generation, int delayMs, CancellationToken ct)
    {
        try { await Task.Delay(delayMs, ct); }
        catch (OperationCanceledException) { return; }
        try { Flush(key, runtime, buffer, generation); }
        catch (Exception ex) { RaiseLog($"[频道:{runtime.Name}] 合并消息投递失败：{ex.Message}"); }
    }

    /// <summary>合并并投递指定 buffer；generation 不匹配说明期间来了新消息，由新定时器负责。</summary>
    private void Flush(string key, ChannelRuntime runtime, MergeBuffer buffer, int? generation = null)
    {
        List<BotMessage> messages;
        lock (buffer.Gate)
        {
            if (generation is not null && generation != buffer.Generation) return;
            if (buffer.Messages.Count == 0) return;
            messages = [.. buffer.Messages];
            buffer.Messages.Clear();
            buffer.Delay?.Cancel();
            buffer.Delay?.Dispose();
            buffer.Delay = null;
            _mergeBuffers.TryRemove(key, out _);
        }

        if (messages.Count == 1)
        {
            Emit(runtime, messages[0], 1);
            return;
        }

        var last = messages[^1];
        var context = string.Join("\n", messages.Take(messages.Count - 1)
            .Select((m, i) => $"[合并消息 {i + 1}/{messages.Count} · {DisplaySender(m)}] {m.Text}"));
        var text = $"[以下是合并消息 - 作为上下文]\n{context}\n[当前消息 - 结合上下文回复]\n{last.Text}";
        Emit(runtime, new BotMessage
        {
            MessageId = last.MessageId,
            ChatId = last.ChatId,
            ChatType = last.ChatType,
            SenderId = last.SenderId,
            SenderName = last.SenderName,
            Text = text,
            Timestamp = last.Timestamp,
            MentionedMe = messages.Any(m => m.MentionedMe),
            RawJson = last.RawJson,
            Attachments = MergeAttachments(messages),
            ImageUrls = MergeImages(messages),
        }, messages.Count);
    }

    private void FlushChannels(string? channelId = null)
    {
        foreach (var (key, buffer) in _mergeBuffers.ToArray())
        {
            if (channelId is not null &&
                !string.Equals(buffer.ChannelId, channelId, StringComparison.OrdinalIgnoreCase))
                continue;
            var runtime = Find(buffer.ChannelId);
            if (runtime is not null) Flush(key, runtime, buffer);
        }
    }

    private void Emit(ChannelRuntime runtime, BotMessage message, int mergedCount)
    {
        message.ChannelId = runtime.Id;
        message.ChannelName = runtime.Name;
        message.SupportsMarkdown = runtime.Config.Capabilities.Markdown;
        MessageReceived?.Invoke(this, new ChannelMessageEventArgs(runtime.Id, message, mergedCount));
    }

    private static string MergeKey(string channelId, string chatId) => $"{channelId}:{chatId}";

    private static string DisplaySender(BotMessage message) =>
        string.IsNullOrWhiteSpace(message.SenderName) ? message.SenderId : message.SenderName;

    private static bool IsCommand(string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('/')) return true;
        var line = trimmed.Split('\n', 2)[0].Trim();
        return line is "新话题" or "换个话题" or "重置上下文";
    }

    private static IReadOnlyList<MessageAttachment>? MergeAttachments(List<BotMessage> messages)
    {
        var items = messages.Where(m => m.Attachments is { Count: > 0 })
            .SelectMany(m => m.Attachments!)
            .ToList();
        return items.Count > 0 ? items : null;
    }

    private static IReadOnlyList<string>? MergeImages(List<BotMessage> messages)
    {
        var items = messages.Where(m => m.ImageUrls is { Count: > 0 })
            .SelectMany(m => m.ImageUrls!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return items.Count > 0 ? items : null;
    }

    private void RaiseLog(string line) => Log?.Invoke(this, line);

    private static string Prefix(ChannelRuntime runtime, string line) =>
        line.StartsWith("[频道", StringComparison.Ordinal) ? line : $"[频道:{runtime.Name}] {line}";

    /// <summary>单个会话的合并缓冲（按频道 + chatId 隔离）。</summary>
    private sealed class MergeBuffer(string channelId)
    {
        public string ChannelId { get; } = channelId;
        public object Gate { get; } = new();
        public List<BotMessage> Messages { get; } = [];
        public CancellationTokenSource? Delay { get; set; }
        public int Generation { get; set; }
    }

    /// <summary>回复发送器：固定走某个频道，并遵守该频道的能力声明。</summary>
    private sealed class ChannelSender(ChannelManager manager, string channelId) : IMessageSender
    {
        public Task SendTextAsync(string chatId, ChatType chatType, string text, string? replyToMessageId,
            CancellationToken ct = default)
        {
            var runtime = manager.Find(channelId)
                ?? throw new InvalidOperationException($"未找到频道 {channelId}");
            var connection = runtime.Connection
                ?? throw new InvalidOperationException($"频道 {runtime.Name} 未启动");
            return connection.SendTextAsync(chatId, chatType, text, replyToMessageId, ct);
        }

        public Task<bool> TrySendForwardAsync(string chatId, ChatType chatType,
            IReadOnlyList<string> sections, string? replyToMessageId = null, CancellationToken ct = default)
        {
            var runtime = manager.Find(channelId);
            if (runtime is null) return Task.FromResult(false);
            // Forward = 合并转发卡片；Merged = 多段合并成一条普通消息。两者任一可用即可尝试。
            if (!runtime.Config.Capabilities.Forward && !runtime.Config.Capabilities.Merged)
                return Task.FromResult(false);
            return runtime.Connection?.TrySendForwardAsync(chatId, chatType, sections, replyToMessageId, ct)
                   ?? Task.FromResult(false);
        }
    }
}

/// <summary>按频道配置创建协议连接（官方 Bot / OneBot11）。</summary>
public static class ChannelFactory
{
    public static IBotConnection Create(ChannelConfig channel)
    {
        if (!string.Equals(channel.Type, "qq", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"频道平台 {channel.Type} 暂不支持（当前只有 qq）");

        if (channel.IsOfficial)
        {
            var (appId, appSecret) = channel.ResolveOfficialCredentials();
            if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(appSecret))
                throw new InvalidOperationException($"频道 {channel.Name} 未配置 AppID / AppSecret");
            return new OfficialBotConnection(new OfficialBotConfig
            {
                Enabled = true,
                AppId = appId,
                AppSecret = appSecret,
                Sandbox = channel.ResolveSandbox(),
                MarkdownSupport = channel.MarkdownSupport,
            });
        }

        if (channel.IsOneBot11)
        {
            var config = channel.OneBot11 ?? new OneBot11Config();
            if (string.IsNullOrWhiteSpace(config.Host))
                throw new InvalidOperationException($"频道 {channel.Name} 未配置 OneBot11 Host");
            return new OneBot11Connection(config);
        }

        throw new NotSupportedException($"频道 {channel.Name} 的传输方式 {channel.Transport} 暂不支持");
    }
}
