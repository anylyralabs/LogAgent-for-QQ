using Avalonia.Controls;

namespace LogAgent.Desktop.Views;

public partial class LogsView : UserControl
{
    public LogsView()
    {
        InitializeComponent();
        // 首次显示时把历史日志灌进列表（Shell 只保留环形缓冲）
        Loaded += (_, _) =>
        {
            if (DataContext is ViewModels.LogsViewModel vm) vm.Refresh();
        };
    }
}
