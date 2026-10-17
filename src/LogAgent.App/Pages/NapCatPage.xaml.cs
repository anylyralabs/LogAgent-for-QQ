using System.Windows;
using System.Windows.Controls;

namespace LogAgent.App.Pages;

public partial class NapCatPage : Page
{
    private readonly NapCatViewModel _vm = new();

    public NapCatPage()
    {
        InitializeComponent();
        DataContext = _vm;
        Unloaded += (_, _) => _vm.StopTimers();
    }

    private void CopyAddress_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_vm.WsAddress);
        }
        catch (Exception)
        {
            // 剪贴板偶尔被占用，忽略
        }
    }
}
