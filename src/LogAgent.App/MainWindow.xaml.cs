using System.ComponentModel;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using LogAgent.App.Pages;
using LogAgent.App.Services;
using LogAgent.Shared.Configuration;

namespace LogAgent.App;

public partial class MainWindow : Window
{
    private readonly Dictionary<string, Func<FrameworkElement>> _pages = new();
    private TrayManager? _tray;
    private EventWaitHandle? _wakeEvent;
    private EventWaitHandle? _hideEvent;
    private bool _trayHidden;
    private bool _uiReady;

    public MainWindow()
    {
        InitializeComponent();
        // 说明：Mica/DWM 材质在部分环境（虚拟机/关闭特效的系统）不可用，
        // 为保证呈现确定性，当前采用纯色 Fluent 方案；材质效果列入 M6（用 Wpf.Ui 库或系统级检测）。

        // 启动参数：--minimized（配合开机自启）
        var startMinimized = Environment.GetCommandLineArgs()
            .Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase))
            || AppConfigStore.Load().StartMinimized;
        if (startMinimized)
            WindowState = WindowState.Minimized;

        // 系统托盘（QQ 式后台驻留）：最小化 → 收进托盘（任务栏不留图标）
        _tray = new TrayManager(RestoreFromTray, () => Close());
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized) MinimizeToTray(showHint: _uiReady);
        };
        Closed += (_, _) =>
        {
            _tray?.Dispose();
            _tray = null;
        };
        StartWakeListener();

        // 启动后自动连接 Bot（延迟几秒让界面就绪）
        Loaded += async (_, _) =>
        {
            _uiReady = true;
            // 开机自启 / 启动时最小化：静默进托盘（不弹提示）
            if (WindowState == WindowState.Minimized) MinimizeToTray(showHint: false);
            await Task.Delay(2000);
            await AppHost.AutoConnectAsync();
        };

        _pages["dashboard"] = () => new DashboardPage();
        _pages["chat"] = () => new ChatPage();
        _pages["providers"] = () => new ProvidersPage();
        _pages["officialbot"] = () => new OfficialBotPage();
        _pages["napcat"] = () => new NapCatPage();
        _pages["knowledge"] = () => new KnowledgePage();
        _pages["skills"] = () => new SkillsPage();
        _pages["memory"] = () => new MemoryPage();
        _pages["sessions"] = () => new SessionsPage();
        _pages["sandbox"] = () => new SandboxPage();
        _pages["logs"] = () => new LogsPage();
        _pages["settings"] = () => new SettingsPage();

        // 退出时优雅停止（先取消关闭 → 停止连接与 NapCat → 真正关闭）
        Closing += OnClosing;

        // 首次启动 → 全屏向导（完成/跳过后进入主界面）
        if (!AppConfigStore.Load().WizardCompleted)
            EnterWizard();
        else
            Navigate("dashboard");
    }

    // ---------- 系统托盘 ----------

    /// <summary>最小化 → 收进系统托盘：隐藏窗口与任务栏按钮，仅留通知区图标。</summary>
    private void MinimizeToTray(bool showHint)
    {
        if (_trayHidden) return;
        _trayHidden = true;
        Hide();
        if (showHint) _tray?.ShowFirstHint();
    }

    /// <summary>从托盘恢复主界面（托盘双击/菜单、再次启动 exe、工具脚本唤醒）。</summary>
    public void RestoreFromTray()
    {
        if (!_trayHidden && IsVisible)
        {
            Activate();
            return;
        }
        _trayHidden = false;
        Show();
        WindowState = WindowState.Normal;
        Activate();
        // 置顶闪现：确保从托盘恢复时窗口浮到最前（截图/验证工具依赖）
        Topmost = true;
        Topmost = false;
    }

    /// <summary>
    /// 监听两个命名事件（供「再次启动 exe」和调试工具使用，避免双开、便于自动化）：
    ///   LogAgent.ShowWindow   → 从托盘恢复主界面
    ///   LogAgent.HideToTray   → 收进系统托盘（截图工具拍完后放回后台）
    /// </summary>
    private void StartWakeListener()
    {
        try
        {
            _wakeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "LogAgent.ShowWindow");
            _hideEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "LogAgent.HideToTray");
            ThreadPool.QueueUserWorkItem(_ => Listen(_wakeEvent, () => Dispatcher.BeginInvoke((Action)RestoreFromTray)));
            ThreadPool.QueueUserWorkItem(_ => Listen(_hideEvent, () => Dispatcher.BeginInvoke((Action)(() => MinimizeToTray(showHint: false)))));
        }
        catch (Exception)
        {
            // 命名事件创建失败：忽略（托盘双击仍可恢复）
        }
    }

    private static void Listen(EventWaitHandle ev, Action onSignal)
    {
        while (true)
        {
            try
            {
                ev.WaitOne();
                onSignal();
            }
            catch (Exception)
            {
                return;
            }
        }
    }

    // ---------- 向导 / 导航 / 退出 ----------

    private void EnterWizard()
    {
        NavList.IsEnabled = false;
        ContentHost.Content = new WizardPage();
    }

    private bool _shutdownDone;

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_shutdownDone) return; // 第二次进入：放行关闭
        e.Cancel = true;
        try
        {
            await AppHost.ShutdownAsync();
        }
        catch (Exception)
        {
            // 停止过程中的异常不阻止退出
        }
        _shutdownDone = true;
        Close();
    }

    public void ExitWizard()
    {
        NavList.IsEnabled = true;
        Navigate("dashboard");
    }

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is ListBoxItem { Tag: string tag })
            Navigate(tag);
    }

    private void Navigate(string tag)
    {
        if (ContentHost is null) return; // InitializeComponent 期间 SelectionChanged 提前触发的保护
        ContentHost.Content = _pages.TryGetValue(tag, out var factory)
            ? factory()
            : new PlaceholderPage(tag, "建设中");
    }
}
