using Avalonia.Controls;

namespace LogAgent.Desktop.Views;

/// <summary>主窗口：左侧导航 + 内容区 + 底部状态栏（对应 WPF 壳的 MainWindow）。</summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // 窗口创建时 DataContext 尚未赋值（App 里 `new MainWindow { DataContext = shell }`），
        // 因此用 DataContextChanged 完成首次导航，保证内容区不是空白。
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ViewModels.ShellViewModel shell) shell.Navigate("dashboard");
        };
    }

    private static readonly string[] NavKeys = ["dashboard", "chat", "providers", "logs", "settings"];

    private void Nav_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not ViewModels.ShellViewModel shell) return;
        if (sender is not ListBox listBox) return;
        var index = listBox.SelectedIndex;
        if (index >= 0 && index < NavKeys.Length) shell.Navigate(NavKeys[index]);
    }
}
