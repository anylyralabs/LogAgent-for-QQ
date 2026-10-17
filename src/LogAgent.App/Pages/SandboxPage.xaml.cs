using System.Windows.Controls;

namespace LogAgent.App.Pages;

public partial class SandboxPage : Page
{
    public SandboxPage()
    {
        InitializeComponent();
        DataContext = new SandboxViewModel();
    }
}
