using Avalonia.Threading;
using LogAgent.Connections;
using LogAgent.Core;
using LogAgent.Shared.Configuration;
using LogAgent.Shared.Platform;

namespace LogAgent.Desktop.ViewModels;

/// <summary>
/// 桌面壳组合根视图模型：持有 <see cref="AgentHost"/>（与 WPF 壳同一套运行时）、
/// 频道管理器、日志缓冲与各页面的子视图模型。
/// 页面切换只切 CurrentPage，运行时不重建（等价于 WPF 壳的 AppHost 单例语义）。
/// </summary>
public sealed class ShellViewModel : ObservableObject
{
    private readonly AgentHost _host;
    private readonly List<string> _logBuffer = [];
    private ChannelManager? _channels;
    private string? _channelFingerprint;
    private object? _currentPage;
    private string _pageTitle = "仪表盘";
    private string _status = "";
    private string _connectionState = "未启动";

    public ShellViewModel()
    {
        _host = new AgentHost();
        _host.Logged += OnHostLog;
        _host.LocalChatReply += OnLocalReply;

        Config = _host.Config;
        Dashboard = new DashboardViewModel(this);
        Chat = new ChatViewModel(this);
        Providers = new ProvidersViewModel(this);
        Logs = new LogsViewModel(this);
        Settings = new SettingsViewModel(this);

        CurrentPage = Dashboard;
        StartOneBotCommand = new RelayCommand(StartOneBotAsync, () => !IsOneBotRunning);
        StopOneBotCommand = new RelayCommand(StopOneBotAsync, () => IsOneBotRunning);
        ConnectionState = DescribeIdleChannels(Config);
        EnsureRuntime();

        _ = Task.Run(async () =>
        {
            await Task.Delay(1500); // 等窗口就绪再自动连接
            if (Config.AutoStartBots) await AutoConnectAsync();
        });
    }

    // ─────────────────────────── 对外状态 ───────────────────────────

    public AppConfig Config { get; private set; }
    public DashboardViewModel Dashboard { get; }
    public ChatViewModel Chat { get; }
    public ProvidersViewModel Providers { get; }
    public LogsViewModel Logs { get; }
    public SettingsViewModel Settings { get; }

    public string PlatformLine =>
        $"{PlatformInfo.Describe()} · {PlatformShell.Current.DisplayName}";

    public string DataDirectory => AppConfigStore.GetConfigDirectory();

    public object? CurrentPage
    {
        get => _currentPage;
        private set
        {
            if (Set(ref _currentPage, value)) Raise(nameof(HasPage));
        }
    }

    public bool HasPage => _currentPage is not null;

    public string PageTitle { get => _pageTitle; private set => Set(ref _pageTitle, value); }

    public string Status { get => _status; private set => Set(ref _status, value); }

    /// <summary>连接状态（仪表盘与侧栏共用）：逐频道展示，官方 Bot 在前。</summary>
    public string ConnectionState { get => _connectionState; private set => Set(ref _connectionState, value); }

    public RelayCommand StartOneBotCommand { get; }
    public RelayCommand StopOneBotCommand { get; }

    public AgentHost Host => _host;
    public IReadOnlyList<string> RecentLogs => _logBuffer;

    public event Action<ChatMessage>? LocalReplied;

    private bool IsOneBotRunning =>
        _channels?.Channels.Any(c => c.Config.IsOneBot11 && c.Running) == true;

    // ─────────────────────────── 导航 ───────────────────────────

    public void Navigate(string key)
    {
        object? page = key switch
        {
            "dashboard" => Dashboard,
            "chat" => Chat,
            "providers" => Providers,
            "logs" => Logs,
            "settings" => Settings,
            _ => new PlaceholderViewModel(key, "该页面在跨平台壳中尚未实现；可用 Windows 桌面版操作，或先编辑 config.json。"),
        };
        PageTitle = key switch
        {
            "dashboard" => "仪表盘",
            "chat" => "对话",
            "providers" => "模型路由",
            "logs" => "运行日志",
            "settings" => "设置",
            _ => "建设中",
        };
        CurrentPage = page;
    }

    // ─────────────────────────── 运行时 / 频道 ───────────────────────────

    private void EnsureRuntime()
    {
        try
        {
            _host.Ensure();
            Status = "运行时已就绪";
        }
        catch (Exception ex)
        {
            Status = $"运行时装配失败：{ex.Message}";
        }
    }

    /// <summary>按频道优先级启动所有启用频道（官方 Bot 优先）。</summary>
    public async Task AutoConnectAsync()
    {
        Config = AppConfigStore.Load();
        try
        {
            var manager = await EnsureChannelsAsync(Config);
            await manager.StartAllAsync();
        }
        catch (Exception ex)
        {
            AppendLog($"[频道] 自动连接失败：{ex.Message}");
        }
        UpdateConnectionState();
    }

    public async Task StartOneBotAsync()
    {
        if (IsOneBotRunning) return;
        Config = AppConfigStore.Load();
        EnsureRuntime();
        var channel = Config.FindChannel(ChannelTransport.OneBot11);
        if (channel is null)
        {
            ConnectionState = "未配置 OneBot11 频道";
            AppendLog("[OneBot11] 未配置频道，请先在配置里添加");
            return;
        }

        try
        {
            var manager = await EnsureChannelsAsync(Config);
            if (!await manager.StartAsync(channel.Id))
            {
                ConnectionState = $"OneBot11 启动失败：{manager.Find(channel.Id)?.LastError}";
                return;
            }
            UpdateConnectionState();
        }
        catch (Exception ex)
        {
            ConnectionState = $"OneBot11 启动失败：{ex.Message}";
            AppendLog($"[OneBot11] 启动失败：{ex.Message}");
        }
    }

    public async Task StopOneBotAsync()
    {
        if (_channels is not null)
        {
            await _host.CutAllSessionsAsync("shutdown");
            var channel = Config.FindChannel(ChannelTransport.OneBot11);
            if (channel is not null) await _channels.StopAsync(channel.Id);
        }
        UpdateConnectionState();
    }

    public async Task SendLocalChatAsync(string text)
    {
        EnsureRuntime();
        await _host.SendLocalChatAsync(text);
    }

    // ─────────────────────────── 频道装配 / 状态 ───────────────────────────

    /// <summary>配置发生实质变化时重建频道管理器，保证改完凭证/端口后生效。</summary>
    private async Task<ChannelManager> EnsureChannelsAsync(AppConfig config)
    {
        var channels = config.Channels ?? [];
        var fingerprint = channels.ChannelFingerprint();
        if (_channels is not null && _channelFingerprint == fingerprint)
            return _channels;

        if (_channels is not null)
        {
            await _channels.DisposeAsync();
            _channels = null;
        }

        var manager = new ChannelManager(channels);
        manager.Log += (_, line) => AppendLog(line);
        manager.MessageReceived += OnChannelMessage;
        _channels = manager;
        _channelFingerprint = fingerprint;
        return manager;
    }

    private async void OnChannelMessage(object? sender, ChannelMessageEventArgs e)
    {
        try
        {
            if (_host.Runtime is { } runtime && _channels is not null)
                await runtime.OnMessageAsync(_channels.CreateSender(e.ChannelId), e.Message);
        }
        catch (Exception ex)
        {
            AppendLog($"[Runtime] 处理消息失败：{ex.Message}");
        }
    }

    private void UpdateConnectionState()
    {
        var statuses = _channels?.Snapshot().Where(s => s.Enabled || s.Running).ToList() ?? [];
        ConnectionState = statuses.Count > 0
            ? string.Join(" · ", statuses.Select(s => $"{s.Name}：{s.StateText}"))
            : DescribeIdleChannels(Config);
        StartOneBotCommand.RaiseCanExecuteChanged();
        StopOneBotCommand.RaiseCanExecuteChanged();
    }

    /// <summary>未启动时的可读提示（只列启用的频道；官方 Bot 自然排在前面）。</summary>
    private static string DescribeIdleChannels(AppConfig config)
    {
        var active = config.ActiveChannels();
        if (active.Count == 0) return "未启用任何频道（设置 → 频道）";
        return string.Join(" · ", active.Select(c => $"{c.Name} 未启动"));
    }

    // ─────────────────────────── 日志 / 回复 ───────────────────────────

    private void OnHostLog(string line) => AppendLog(line);

    private void OnLocalReply(string text) =>
        Dispatcher.UIThread.Post(() => LocalReplied?.Invoke(new ChatMessage(ChatRole.Agent, text, DateTime.Now)));

    private DateTime _lastLogRefresh = DateTime.MinValue;
    private bool _logRefreshPending;

    public void AppendLog(string line)
    {
        var entry = $"{DateTime.Now:HH:mm:ss} {line}";
        Dispatcher.UIThread.Post(() =>
        {
            _logBuffer.Add(entry);
            if (_logBuffer.Count > 1000) _logBuffer.RemoveAt(0);

            // 节流：高频日志（工具调用/沙箱审计）下避免每次都重建列表导致 UI 卡顿；
            // 同时安排一次「尾部刷新」，保证最后一条日志最终也会显示。
            var now = DateTime.UtcNow;
            if (now - _lastLogRefresh >= TimeSpan.FromMilliseconds(500))
            {
                _lastLogRefresh = now;
                Logs.Refresh();
                Raise(nameof(RecentLogs));
            }
            else if (!_logRefreshPending)
            {
                _logRefreshPending = true;
                _ = Task.Delay(500).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
                {
                    _logRefreshPending = false;
                    _lastLogRefresh = DateTime.UtcNow;
                    Logs.Refresh();
                    Raise(nameof(RecentLogs));
                }));
            }
        });
    }

    // ─────────────────────────── 退出 ───────────────────────────

    public async Task ShutdownAsync()
    {
        try
        {
            if (_channels is not null) await _channels.DisposeAsync();
            await _host.CutAllSessionsAsync("shutdown");
        }
        catch (Exception)
        {
            // 退出路径尽力而为
        }
        _channels = null;
        await _host.DisposeAsync();
    }
}
