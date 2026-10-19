namespace LogAgent.Desktop.ViewModels;

/// <summary>占位页（跨平台壳尚未实现的页面）。</summary>
public sealed class PlaceholderViewModel(string title, string description) : ObservableObject
{
    public string Title { get; } = title;
    public string Description { get; } = description;
}
