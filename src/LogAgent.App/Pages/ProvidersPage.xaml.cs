using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LogAgent.App.Pages;

public partial class ProvidersPage : Page
{
    public ProvidersPage()
    {
        InitializeComponent();
        DataContext = new ProvidersViewModel();
    }

    private ProvidersViewModel Vm => (ProvidersViewModel)DataContext;

    /// <summary>添加模型：独立弹窗（表单 + 测试连接），保存后加入列表。</summary>
    private void AddProvider_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ProviderEditWindow { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true || dialog.Result is null) return;
        var item = new ProviderItem(dialog.Result);
        Vm.Providers.Add(item);
        Vm.SelectedProvider = item;
    }

    private void EditProvider_Click(object sender, RoutedEventArgs e) => EditSelected();

    /// <summary>双击一行 → 打开编辑弹窗。</summary>
    private void ProvidersGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm.SelectedProvider is not null) EditSelected();
    }

    /// <summary>编辑选中 Provider：弹窗就地更新（保持列表引用与勾选状态）。</summary>
    private void EditSelected()
    {
        if (Vm.SelectedProvider is not { } item) return;
        var dialog = new ProviderEditWindow(item.ToConfig()) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true || dialog.Result is null) return;
        var config = dialog.Result;
        item.Name = config.Name;
        item.Endpoint = config.Endpoint;
        item.ApiKey = config.ApiKey;
        item.ChatModel = config.ChatModel;
        item.EmbeddingModel = config.EmbeddingModel;
        item.MaxTokens = config.MaxTokens;
        item.Temperature = config.Temperature;
        item.TimeoutSeconds = config.TimeoutSeconds;
        item.Enabled = config.Enabled;
    }
}
