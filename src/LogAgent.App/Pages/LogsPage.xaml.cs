using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using LogAgent.App.Services;

namespace LogAgent.App.Pages;

/// <summary>运行日志页：全局日志实时流（历史来自 AppHost 环形缓冲）+ 关键词过滤 + 自动滚动。</summary>
public partial class LogsPage : Page
{
    private readonly ObservableCollection<string> _visible = [];
    private readonly List<string> _buffer = [];
    private string[] _keywords = [];

    private const int MaxBuffer = 2000;

    public LogsPage()
    {
        InitializeComponent();
        LogList.ItemsSource = _visible;

        foreach (var line in AppHost.GetRecentLogs())
        {
            _buffer.Add(line);
            if (Matches(line)) _visible.Add(line);
        }
        UpdateCount();

        AppHost.Logged += OnLogged;
        Unloaded += (_, _) => AppHost.Logged -= OnLogged;
        if (AutoScrollCheck.IsChecked == true) ScrollToEnd();
    }

    private void OnLogged(string line)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _buffer.Add(line);
            if (_buffer.Count > MaxBuffer) _buffer.RemoveRange(0, _buffer.Count - MaxBuffer);
            if (!Matches(line)) return;
            _visible.Add(line);
            while (_visible.Count > MaxBuffer) _visible.RemoveAt(0);
            UpdateCount();
            if (AutoScrollCheck.IsChecked == true) ScrollToEnd();
        });
    }

    private bool Matches(string line)
        => _keywords.Length == 0 || _keywords.All(k => line.Contains(k, StringComparison.OrdinalIgnoreCase));

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _keywords = FilterBox.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _visible.Clear();
        foreach (var line in _buffer)
            if (Matches(line)) _visible.Add(line);
        UpdateCount();
        if (AutoScrollCheck.IsChecked == true) ScrollToEnd();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _buffer.Clear();
        _visible.Clear();
        UpdateCount();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_visible.Count == 0) return;
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, _visible));
        }
        catch (Exception)
        {
            // 剪贴板偶尔被其他进程占用，忽略
        }
    }

    private void UpdateCount() => CountText.Text = $"显示 {_visible.Count} / 缓冲 {_buffer.Count} 条";

    private void ScrollToEnd()
    {
        if (_visible.Count == 0) return;
        LogList.ScrollIntoView(_visible[^1]);
    }
}
