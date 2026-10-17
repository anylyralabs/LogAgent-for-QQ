using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LogAgent.App.Services;
using LogAgent.Shared.Configuration;

namespace LogAgent.App.Pages;

/// <summary>对话页：本机控制台（与 QQ 消息共用同一条 Agent 管线）。</summary>
public partial class ChatPage : Page
{
    private readonly ChatViewModel _vm = ChatViewModel.Shared;
    private readonly DispatcherTimer _statusTimer;

    public ChatPage()
    {
        InitializeComponent();
        DataContext = _vm;
        _vm.ScrollRequested += OnScrollRequested;

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _statusTimer.Tick += (_, _) => RefreshStatus();

        Loaded += (_, _) =>
        {
            RefreshStatus();
            _statusTimer.Start();
            InputBox.Focus();
            Dispatcher.InvokeAsync(() => MessagesScroll.ScrollToEnd(), DispatcherPriority.Background);
        };
        Unloaded += (_, _) =>
        {
            _statusTimer.Stop();
            _vm.ScrollRequested -= OnScrollRequested;
        };
    }

    private void OnScrollRequested()
        => Dispatcher.InvokeAsync(() => MessagesScroll.ScrollToEnd(), DispatcherPriority.Background);

    private void Input_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return; // 输入法组词时 Key == ImeProcessed，不触发发送
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) return; // Shift+Enter 换行
        e.Handled = true;
        _vm.SendCommand.Execute(null);
    }

    private void QuickInput_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string text }) return;
        FillInput(text);
    }

    private void QuickLog_Click(object sender, RoutedEventArgs e)
        => FillInput("分析日志\n"
            + "ERROR 2026-10-15 14:20:01 [db-pool] connection timeout after 30000ms host=10.0.0.8\n"
            + "   at com.app.Db.GetConnection(Db.java:88)\n"
            + "   at com.app.Api.Handle(Api.java:42)\n"
            + "   at com.app.Main.Run(Main.java:17)");

    private void QuickRemember_Click(object sender, RoutedEventArgs e)
        => FillInput("记住：数据库连接池耗尽时，优先检查 maxActive 与等待超时配置");

    private void FillInput(string text)
    {
        _vm.InputText = text;
        InputBox.Focus();
        InputBox.CaretIndex = InputBox.Text.Length;
    }

    private void RefreshStatus()
    {
        var config = AppConfigStore.Load();

        // 模型通道：Chat 任务优先链 → Provider 名称
        var rule = config.Rules.FirstOrDefault(r => r.TaskType == TaskType.Chat);
        var names = (rule?.ProviderChain ?? [])
            .Select(id => config.Providers.FirstOrDefault(p => p.Id == id)?.Name ?? id)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToList();
        if (names.Count > 0)
        {
            ModelChannelText.Text = string.Join(" → ", names);
            ModelChannelText.Foreground = (Brush)FindResource("OkFill");
            ModelHint.Visibility = Visibility.Collapsed;
        }
        else
        {
            ModelChannelText.Text = "未配置";
            ModelChannelText.Foreground = (Brush)FindResource("WarnFill");
            ModelHint.Visibility = Visibility.Visible;
        }

        SetState(OneBotStateText, AppHost.OneBotServerStarted ? "运行中" : "未启动");
        SetState(OfficialStateText, AppHost.OfficialBotRunning ? "已连接" : "未连接");
    }

    private void SetState(TextBlock target, string text)
    {
        target.Text = text;
        target.Foreground = (Brush)FindResource(text is "运行中" or "已连接" ? "OkFill" : "TextSecondary");
    }
}
