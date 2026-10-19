using Avalonia.Controls;

namespace LogAgent.Desktop.Views;

public partial class DashboardView : UserControl
{
    public DashboardView() => InitializeComponent();

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (DataContext is ViewModels.DashboardViewModel vm) vm.Refresh();
    }
}
