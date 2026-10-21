namespace LogAgent.Shared.Configuration;

/// <summary>
/// 频道传输方式常量。一个频道（<see cref="ChannelConfig"/>）可以换传输而不换平台：
/// 例如 QQ 平台既能走官方 Bot，也能走 NapCat/OneBot11。
/// </summary>
public static class ChannelTransport
{
    /// <summary>QQ 开放平台官方 Bot（WebSocket 网关，合规，官方优先支持）。</summary>
    public const string Official = "official";

    /// <summary>OneBot11 反向 WebSocket（NapCat 等第三方协议端）。</summary>
    public const string OneBot11 = "onebot11";

    public static bool IsOfficial(string? transport) =>
        string.Equals(transport?.Trim(), Official, StringComparison.OrdinalIgnoreCase);

    public static bool IsOneBot11(string? transport) =>
        string.Equals(transport?.Trim(), OneBot11, StringComparison.OrdinalIgnoreCase);

    /// <summary>规整传输名：空值按官方 Bot 处理（官方优先），其余保留小写以便报错时仍可读。</summary>
    public static string Normalize(string? transport)
    {
        var text = transport?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(text) ? Official : text;
    }

    public static string DisplayName(string? transport) => Normalize(transport) switch
    {
        Official => "官方 Bot",
        OneBot11 => "OneBot11 / NapCat",
        var other => other,
    };

    /// <summary>默认优先级：官方 Bot 高于 OneBot11（同时启用时官方 Bot 先启动、优先主动发送）。</summary>
    public static int DefaultPriority(string? transport) => IsOfficial(transport) ? 100 : 50;
}

/// <summary>
/// 频道（Channel）—— 对「消息平台接入端点」的统一抽象，取自 OpenClaw 的 channel 概念：
/// 一个频道 = 一种平台 + 一组账号/凭证 + 一套启停与探活方式。
///
/// <para>为什么要有这一层：以前配置里并列写着 <c>OneBot11</c> 与 <c>OfficialBot</c>，
/// 名字直接暴露了「协议」而不是「渠道」，新增平台就得改 <see cref="AppConfig"/> 的字段。
/// 改成频道集合后，配置形如 <c>Channels: [{ Id: "qq-official", Transport: "official", … }]</c>，
/// 与 OpenClaw 的 <c>channels.&lt;id&gt;</c> 一致，加平台不再动模型。</para>
///
/// <para>官方 Bot 是默认传输方式（<see cref="Transport"/> 缺省为 official、优先级 100），
/// OneBot11/NapCat 作为同等能力的备用频道，两者可以同时启用。</para>
/// </summary>
public sealed class ChannelConfig
{
    /// <summary>频道 ID（配置里的键，稳定不变；如 <c>qq-official</c>）。</summary>
    public string Id { get; set; } = "";

    /// <summary>频道类型（决定用哪个平台实现）。目前支持 <c>qq</c>。</summary>
    public string Type { get; set; } = "qq";

    /// <summary>显示名（UI 用，可改）。</summary>
    public string Name { get; set; } = "";

    public bool Enabled { get; set; }

    /// <summary>
    /// 接入方式：<c>official</c>（QQ 开放平台 Bot，默认）或 <c>onebot11</c>（NapCat 反向 WS）。
    /// </summary>
    public string Transport { get; set; } = ChannelTransport.Official;

    /// <summary>
    /// 频道优先级：数值越大越优先。官方 Bot 默认 100，OneBot11 默认 50。
    /// <para>用途：多频道同时启用时决定启动顺序，以及「主动发送」时优先使用哪个已连接频道。
    /// 收到的消息永远从原频道回复，不受此字段影响。</para>
    /// </summary>
    public int Priority { get; set; } // 0 = 未设置，NormalizeChannels 按传输方式补默认值

    /// <summary>OpenClaw 风格的通用凭证串（<c>AppID:AppSecret</c>）；显式 AppId/AppSecret 优先。</summary>
    public string Token { get; set; } = "";

    /// <summary>官方 Bot 的 AppID（OpenClaw 的 <c>appId</c> 同义）。</summary>
    public string AppId { get; set; } = "";

    /// <summary>官方 Bot 的 AppSecret（OpenClaw 的 <c>clientSecret</c> 同义）。</summary>
    public string AppSecret { get; set; } = "";

    /// <summary>官方 Bot 沙箱环境；null = 未显式配置（读取旧字段兜底）。</summary>
    public bool? Sandbox { get; set; }

    /// <summary>
    /// 官方 Bot 是否使用 Markdown 消息（<c>msg_type=2</c>，OpenClaw 的 <c>markdownSupport</c>）。
    /// 平台拒绝 Markdown 时自动回退纯文本；仅对 official 传输生效。
    /// </summary>
    public bool MarkdownSupport { get; set; } = true;

    /// <summary>
    /// 是否合并短时间内的连续群消息为一次 Agent 轮次（OpenClaw 的 inbound merge）。
    /// 仅对群聊生效；命令消息会立即处理，不参与合并。
    /// </summary>
    public bool MergeMessages { get; set; } = true;

    /// <summary>合并窗口（毫秒）：窗口内没有新消息就落盘处理。</summary>
    public int MergeWindowMs { get; set; } = 600;

    public OneBot11Config OneBot11 { get; set; } = new();

    /// <summary>
    /// 旧版频道内嵌官方配置：仅用于读取未提交版本生成的配置。
    /// 新代码请用 <see cref="AppId"/> / <see cref="AppSecret"/>。
    /// </summary>
    public OfficialBotConfig Official { get; set; } = new();

    /// <summary>频道能力声明（UI 与回复策略用；不参与连接）。</summary>
    public ChannelCapabilities Capabilities { get; set; } = new();

    public bool IsOfficial => ChannelTransport.IsOfficial(Transport);

    public bool IsOneBot11 => ChannelTransport.IsOneBot11(Transport);

    public bool IsTransportSupported => IsOfficial || IsOneBot11;

    /// <summary>
    /// 解析官方 Bot 凭证，兼容三种写法（优先级从高到低）：
    /// ① 频道级 AppId/AppSecret；② OpenClaw 的 <c>AppID:AppSecret</c> Token；③ 旧版内嵌 Official。
    /// </summary>
    public (string AppId, string AppSecret) ResolveOfficialCredentials()
    {
        if (!string.IsNullOrWhiteSpace(AppId) || !string.IsNullOrWhiteSpace(AppSecret))
            return (AppId?.Trim() ?? "", AppSecret ?? "");

        if (!string.IsNullOrWhiteSpace(Token) && Token.Contains(':'))
        {
            var parts = Token.Split(':', 2);
            return (parts[0].Trim(), parts[1].Trim());
        }

        return (Official.AppId ?? "", Official.AppSecret ?? "");
    }

    /// <summary>解析沙箱开关：频道级显式值优先，否则沿用旧版内嵌配置。</summary>
    public bool ResolveSandbox() => Sandbox ?? Official.Sandbox;

    /// <summary>写入官方 Bot 凭证（UI 保存用，同时保持旧字段同步以兼容回退版本）。</summary>
    public void SetOfficialCredentials(string appId, string appSecret, bool sandbox)
    {
        AppId = appId?.Trim() ?? "";
        AppSecret = appSecret ?? "";
        Sandbox = sandbox;
        Token = ""; // 显式表单是唯一真相源，避免清空 AppId 后又回退到旧 Token
        Official.AppId = AppId;
        Official.AppSecret = AppSecret;
        Official.Sandbox = sandbox;
    }

    /// <summary>迁移：把旧版顶层 OneBot11 配置搬进本频道。</summary>
    public void AdoptLegacy(OneBot11Config? oneBot)
    {
        if (oneBot is null) return;
        OneBot11 = oneBot;
        Enabled = oneBot.Enabled;
    }

    /// <summary>迁移：把旧版顶层 OfficialBot 配置搬进本频道。</summary>
    public void AdoptLegacy(OfficialBotConfig? official)
    {
        if (official is null) return;
        Enabled = official.Enabled;
        AppId = official.AppId;
        AppSecret = official.AppSecret;
        Sandbox = official.Sandbox;
        MarkdownSupport = official.MarkdownSupport;
        Official = official;
    }
}

/// <summary>频道能力声明：哪些消息形态可用（决定回复时能否用合并转发等）。</summary>
public sealed class ChannelCapabilities
{
    /// <summary>群聊。</summary>
    public bool Groups { get; set; } = true;

    /// <summary>私聊。</summary>
    public bool Direct { get; set; } = true;

    /// <summary>合并转发卡片（官方 Bot 不支持，OneBot11 支持）。</summary>
    public bool Forward { get; set; }

    /// <summary>把多段内容合并成一条普通消息发送（官方 Bot 用，规避 5 次被动回复上限）。</summary>
    public bool Merged { get; set; }

    /// <summary>Markdown 渲染（官方 Bot 的 <c>msg_type=2</c>，需平台权限）。</summary>
    public bool Markdown { get; set; }

    /// <summary>主动发送（无 msg_id）。用户可在 QQ 客户端关闭，关闭后平台会拒绝。</summary>
    public bool Proactive { get; set; } = true;

    /// <summary>群聊主动消息（官方 Bot 需群主在 QQ 里开启「机器人主动在群聊内发言」）。</summary>
    public bool GroupProactive { get; set; } = true;

    /// <summary>富文本/图片。</summary>
    public bool Media { get; set; } = true;

    /// <summary>按传输方式给默认值：合并转发 / 合并发送是协议能力，随传输方式确定。</summary>
    public void ApplyTransportDefaults(string transport)
    {
        Forward = ChannelTransport.IsOneBot11(transport);
        Merged = ChannelTransport.IsOfficial(transport);
        if (!ChannelTransport.IsOfficial(transport)) Markdown = false;
    }
}

/// <summary>
/// 旧配置字段 → 频道模型的读取垫片。
///
/// <para>为什么需要：<c>AppConfig.OneBot11</c> / <c>OfficialBot</c> 已被标记
/// <see cref="ObsoleteAttribute"/>，但历史 UI / 工具里还有读取代码。
/// 这里给出「按频道取值」的只读快照，避免调用方误改临时对象后以为已经落盘；
/// 写入一律走 <see cref="GetOrAddChannel"/>。新代码请直接用 <c>Config.Channels</c>。</para>
/// </summary>
public static class ChannelConfigExtensions
{
    /// <summary>当前生效的 OneBot11 配置（优先启用的 onebot11 频道；只读快照）。</summary>
    public static OneBot11Config EffectiveOneBot11(this AppConfig config)
    {
        var channel = config.FindChannel(ChannelTransport.OneBot11);
        if (channel is null) return new OneBot11Config();
        var source = channel.OneBot11 ?? new OneBot11Config();
        return new OneBot11Config
        {
            Enabled = channel.Enabled,
            Host = source.Host,
            Port = source.Port,
            Path = source.Path,
            Token = source.Token,
        };
    }

    /// <summary>当前生效的官方 Bot 配置（优先启用的 official 频道；只读快照）。</summary>
    public static OfficialBotConfig EffectiveOfficialBot(this AppConfig config)
    {
        var channel = config.FindChannel(ChannelTransport.Official);
        if (channel is null) return new OfficialBotConfig();
        var (appId, appSecret) = channel.ResolveOfficialCredentials();
        return new OfficialBotConfig
        {
            Enabled = channel.Enabled,
            AppId = appId,
            AppSecret = appSecret,
            Sandbox = channel.ResolveSandbox(),
            MarkdownSupport = channel.MarkdownSupport,
        };
    }

    /// <summary>找指定传输方式的频道：优先启用的，其次第一个；没有返回 null。</summary>
    public static ChannelConfig? FindChannel(this AppConfig config, string transport)
    {
        config.NormalizeChannels();
        var matches = config.Channels!
            .Where(c => string.Equals(c.Transport, ChannelTransport.Normalize(transport), StringComparison.OrdinalIgnoreCase))
            .ToList();
        return matches.FirstOrDefault(c => c.Enabled) ?? matches.FirstOrDefault();
    }

    /// <summary>
    /// 取指定传输方式的频道；没有就新建一个（未启用）。
    /// <para>UI 保存配置时必须用这个拿真实对象，不能改 <see cref="EffectiveOneBot11"/> 等快照。</para>
    /// </summary>
    public static ChannelConfig GetOrAddChannel(this AppConfig config, string transport)
    {
        config.NormalizeChannels();
        var existing = config.FindChannel(transport);
        if (existing is not null) return existing;

        var normalized = ChannelTransport.Normalize(transport);
        var channel = ChannelTransport.IsOfficial(normalized)
            ? AppConfig.CreateOfficialChannel()
            : AppConfig.CreateOneBotChannel();
        channel.Id = UniqueId(config, channel.Id);
        config.Channels!.Add(channel);
        return channel;
    }

    /// <summary>
    /// 频道配置指纹：宿主用它判断「保存后是否需要重建连接」。
    /// 只包含会影响连接/主动发送的字段，不包含显示名与能力声明。
    /// </summary>
    public static string ChannelFingerprint(this IEnumerable<ChannelConfig> channels) => string.Join("|",
        channels.Select(c =>
            $"{c.Id}:{c.Enabled}:{c.Transport}:{c.Priority}:{c.Token}:{c.AppId}:{c.AppSecret}:{c.Sandbox}:" +
            $"{c.MarkdownSupport}:{c.MergeMessages}:{c.MergeWindowMs}:" +
            $"{c.OneBot11.Host}:{c.OneBot11.Port}:{c.OneBot11.Path}:{c.OneBot11.Token}"));

    /// <summary>把频道集合里「是否有启用的 X」这类判断集中到一处。</summary>
    public static bool AnyChannelWithTransport(this AppConfig config, string transport)
    {
        config.NormalizeChannels();
        var normalized = ChannelTransport.Normalize(transport);
        return config.Channels!.Any(c => c.Enabled &&
            string.Equals(c.Transport, normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static string UniqueId(AppConfig config, string baseId)
    {
        var ids = config.Channels!.Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!ids.Contains(baseId)) return baseId;
        for (var i = 2; ; i++)
        {
            var candidate = $"{baseId}-{i}";
            if (!ids.Contains(candidate)) return candidate;
        }
    }
}
