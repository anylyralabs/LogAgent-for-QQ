using System.Windows.Controls;

namespace LogAgent.App.Pages;

public partial class MemoryPage : Page
{
    public MemoryPage()
    {
        InitializeComponent();
        DataContext = new MemoryViewModel();
    }
}
