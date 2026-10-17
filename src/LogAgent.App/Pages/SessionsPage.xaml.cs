using System.Windows.Controls;

namespace LogAgent.App.Pages;

public partial class SessionsPage : Page
{
    public SessionsPage()
    {
        InitializeComponent();
        DataContext = new SessionsViewModel();
    }
}
