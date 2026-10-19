using System.Collections.ObjectModel;
using LogAgent.Shared.Configuration;
using LogAgent.Shared.Platform;

namespace LogAgent.Desktop.ViewModels;

/// <summary>运行日志页：显示 AgentHost 全量日志（环形 1000 条），支持筛选与清屏。</summary>
public sealed class LogsViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private string _filter = "";

    public LogsViewModel(ShellViewModel shell)
    {
        _shell = shell;
        ClearCommand = new RelayCommand(() => Lines.Clear());
    }

    public ObservableCollection<string> Lines { get; } = [];

    public string Filter
    {
        get => _filter;
        set
        {
            if (Set(ref _filter, value)) Refresh();
        }
    }

    public RelayCommand ClearCommand { get; }

    /// <summary>从 Shell 的环形缓冲重建可见行（筛选按子串，不区分大小写）。</summary>
    public void Refresh()
    {
        var source = _shell.RecentLogs;
        var filtered = string.IsNullOrWhiteSpace(_filter)
            ? source
            : source.Where(l => l.Contains(_filter, StringComparison.OrdinalIgnoreCase)).ToList();

        Lines.Clear();
        // 只保留最近 400 行，避免 UI 卡顿
        foreach (var line in filtered.TakeLast(400))
            Lines.Add(line);
    }
}
