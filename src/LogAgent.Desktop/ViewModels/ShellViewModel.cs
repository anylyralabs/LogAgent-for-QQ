using Avalonia.Threading;
using LogAgent.Connections;
using LogAgent.Core;
using LogAgent.Shared.Configuration;
using LogAgent.Shared.Messaging;
using LogAgent.Shared.Platform;

namespace LogAgent.Desktop.ViewModels;

/// <summary>
/// 桌面壳组合根视图模型：持有 <see cref="AgentHost"/>（与 WPF 壳同一套运行时）、
/// OneBot11 连接、日志缓冲与各页面的子视图模型。
/// 页面切换只切 CurrentPage，运行时不重建（等价于 WPF 壳的 AppHost 单例语义）。
/// </summary>
public sealed class ShellViewModel : ObservableObject
{
    private readonly AgentHost _host;
    private readonly List<string> _logBuffer = [];
    private OneBot11Connection? _oneBot;
    private OfficialBotConnection? _officialBot;
    private object? _currentPage;
    private string _pageTitle = "仪表盘";
    private string _status = "";
    private string _connectionState = "未启动";
    private bool _oneBotRunning;

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
        StartOneBotCommand = new RelayCommand(StartOneBotAsync, () => !_oneBotRunning);
        StopOneBotCommand = new RelayCommand(StopOneBotAsync, () => _oneBotRunning);
        // 按当前配置初始化状态栏文案（未启动连接时的可读提示）
        ConnectionState = Config.OneBot11.Enabled
            ? $"OneBot11 未启动（配置端口 {Config.OneBot11.Port}）"
            : "OneBot11 未启用（配置文件里 OneBot11.Enabled = false）";
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

    /// <summary>连接状态（仪表盘与侧栏共用）。</summary>
    public string ConnectionState { get => _connectionState; private set => Set(ref _connectionState, value); }

    public RelayCommand StartOneBotCommand { get; }
    public RelayCommand StopOneBotCommand { get; }

    public AgentHost Host => _host;
    public IReadOnlyList<string> RecentLogs => _logBuffer;

    public event Action<ChatMessage>? LocalReplied;

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

    // ─────────────────────────── 运行时 / 连接 ───────────────────────────

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

    public async Task AutoConnectAsync()
    {
        Config = AppConfigStore.Load();
        if (Config.OneBot11.Enabled && !_oneBotRunning)
            await StartOneBotAsync();
        if (Config.OfficialBot.Enabled && _officialBot is null)
        {
            try
            {
                await StartOfficialBotAsync();
            }
            catch (Exception ex)
            {
                AppendLog($"[官方Bot] 连接失败：{ex.Message}");
            }
        }
    }

    public async Task StartOneBotAsync()
    {
        if (_oneBotRunning) return;
        Config = AppConfigStore.Load();
        EnsureRuntime();
        try
        {
            var connection = new OneBot11Connection(Config.OneBot11);
            connection.ConnectionLog += (_, line) => AppendLog(line);
            connection.MessageReceived += async (_, e) =>
            {
                try
                {
                    if (_host.Runtime is { } runtime)
                        await runtime.OnMessageAsync(new Bridge(() => _oneBot, () => _officialBot), e.Message);
                }
                catch (Exception ex)
                {
                    AppendLog($"[Runtime] 处理消息失败：{ex.Message}");
                }
            };
            await connection.StartAsync();
            _oneBot = connection;
            _oneBotRunning = true;
            ConnectionState = $"OneBot11 监听 {Config.OneBot11.Host}:{Config.OneBot11.Port}（等待 NapCat 连入）";
            StartOneBotCommand.RaiseCanExecuteChanged();
            StopOneBotCommand.RaiseCanExecuteChanged();
        }
        catch (Exception ex)
        {
            ConnectionState = $"OneBot11 启动失败：{ex.Message}";
            AppendLog($"[OneBot11] 启动失败：{ex.Message}");
        }
    }

    public async Task StopOneBotAsync()
    {
        if (_oneBot is not null)
        {
            await _host.CutAllSessionsAsync("shutdown");
            await _oneBot.StopAsync();
            await _oneBot.DisposeAsync();
            _oneBot = null;
        }
        _oneBotRunning = false;
        ConnectionState = "未启动";
        StartOneBotCommand.RaiseCanExecuteChanged();
        StopOneBotCommand.RaiseCanExecuteChanged();
    }

    private async Task StartOfficialBotAsync()
    {
        var config = AppConfigStore.Load().OfficialBot;
        if (string.IsNullOrWhiteSpace(config.AppId) || string.IsNullOrWhiteSpace(config.AppSecret))
        {
            AppendLog("[官方Bot] 未配置 AppID / AppSecret，跳过");
            return;
        }

        var bot = new OfficialBotConnection(config);
        bot.ConnectionLog += (_, line) => AppendLog(line);
        bot.MessageReceived += async (_, e) =>
        {
            try
            {
                if (_host.Runtime is { } runtime)
                    await runtime.OnMessageAsync(new Bridge(() => _oneBot, () => _officialBot), e.Message);
            }
            catch (Exception ex)
            {
                AppendLog($"[Runtime] 处理消息失败：{ex.Message}");
            }
        };
        EnsureRuntime();
        await bot.StartAsync();
        _officialBot = bot;
        AppendLog("[官方Bot] 已启动");
    }

    public async Task SendLocalChatAsync(string text)
    {
        EnsureRuntime();
        await _host.SendLocalChatAsync(text);
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
            await StopOneBotAsync();
        }
        catch (Exception)
        {
            // 退出路径尽力而为
        }
        if (_officialBot is not null)
        {
            try { await _officialBot.StopAsync(); } catch (Exception) { /* ignore */ }
            _officialBot = null;
        }
        await _host.DisposeAsync();
    }

    /// <summary>把 Agent 回复发回在线通道（OneBot 优先，官方 Bot 兜底）。</summary>
    private sealed class Bridge(Func<OneBot11Connection?> oneBot, Func<OfficialBotConnection?> official)
        : IBotConnectionFactory
    {
        public async Task SendTextAsync(string chatId, ChatType chatType, string text,
            string? replyToMessageId, CancellationToken ct = default)
        {
            // 优先用「已连入」的通道：OneBot 在监听但 NapCat 未连入时，应回落到官方 Bot
            if (oneBot() is { IsConnected: true } conn)
            {
                await conn.SendTextAsync(chatId, chatType, text, replyToMessageId, ct);
                return;
            }
            if (official() is { IsConnected: true } bot)
                await bot.SendTextAsync(chatId, chatType, text, replyToMessageId, ct);
        }

        public async Task<bool> TrySendForwardAsync(string chatId, ChatType chatType,
            IReadOnlyList<string> sections, CancellationToken ct = default)
        {
            if (oneBot() is { IsConnected: true } conn) return await conn.SendForwardAsync(chatId, chatType, sections, ct);
            return false; // 官方 Bot 无合并转发
        }
    }
}
