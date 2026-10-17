using System.Windows.Controls;

namespace LogAgent.App.Pages;

public partial class PlaceholderPage : Page
{
    public PlaceholderPage(string title, string description)
    {
        InitializeComponent();
        TitleText.Text = title;
        DescText.Text = description;
    }
}
