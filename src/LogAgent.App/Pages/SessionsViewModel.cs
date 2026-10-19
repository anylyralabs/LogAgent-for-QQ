using System.Collections.ObjectModel;
using System.IO;
using LogAgent.App.Infrastructure;
using LogAgent.App.Services;
using LogAgent.Memory;
using LogAgent.Shared.Configuration;

namespace LogAgent.App.Pages;

public sealed class SessionRowItem(SessionStore.SessionRow row) : ObservableObject
{
    public string ChatId { get; } = row.ChatId;
    public string ChatType { get; } = row.ChatType;
    public string StartTime { get; } = DateTimeOffset.Parse(row.StartTime).ToLocalTime().ToString("MM-dd HH:mm");
    public string EndTime { get; } = row.EndTime is null ? "进行中" : DateTimeOffset.Parse(row.EndTime).ToLocalTime().ToString("MM-dd HH:mm");
    public string CutReason { get; } = row.CutReason ?? "";
    public int MessageCount { get; } = row.MessageCount;
    public bool IsActive { get; } = row.EndTime is null;

    private string _summary = row.Summary ?? "";
    public string Summary { get => _summary; set => Set(ref _summary, value); }
}

public sealed class SessionsViewModel : ObservableObject
{
    public ObservableCollection<SessionRowItem> Sessions { get; } = [];

    public SessionRowItem? SelectedSession
    {
        get => _selected;
        set => Set(ref _selected, value);
    }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand CutActiveCommand { get; }
    public RelayCommand ExportTrainingCommand { get; }

    private SessionRowItem? _selected;

    public SessionsViewModel()
    {
        RefreshCommand = new RelayCommand(Load);
        CutActiveCommand = new RelayCommand(() => _ = CutSelectedAsync(), () => SelectedSession is { IsActive: true });
        ExportTrainingCommand = new RelayCommand(() => _ = ExportTrainingAsync());
        Load();
    }

    /// <summary>导出训练数据：格式选择对话框 → 强制脱敏 → JSONL + 审计。</summary>
    private static async Task ExportTrainingAsync()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出训练数据（JSONL）",
            Filter = "OpenAI Chat|*.jsonl|ShareGPT|*.jsonl|Alpaca|*.jsonl|DPO 对|*.jsonl",
            FilterIndex = 1,
            FileName = $"logagent-training-{DateTime.Now:yyyyMMdd-HHmm}.jsonl",
        };
        var owner = System.Windows.Application.Current?.MainWindow;
        var ok = owner is not null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        if (ok != true) return;

        var format = dialog.FilterIndex switch
        {
            2 => LogAgent.Training.ExportFormat.ShareGpt,
            3 => LogAgent.Training.ExportFormat.Alpaca,
            4 => LogAgent.Training.ExportFormat.DpoPair,
            _ => LogAgent.Training.ExportFormat.OpenAiChat,
        };

        var db = new Database(Path.Combine(AppConfigStore.GetDataDirectory(), "logagent.db"));
        var exporter = new LogAgent.Training.TrainingExporter(new SessionStore(db));
        var result = await exporter.ExportAsync(dialog.FileName, new LogAgent.Training.TrainingExporter.ExportOptions(
            format, ["none", "adopted"], Redact: true));
        System.Windows.MessageBox.Show(
            $"导出完成 ✅\n文件：{result.Path}\n样本行：{result.Lines}（跳过 {result.Skipped}）\n已强制脱敏并写入审计记录。",
            "训练数据导出", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
    }

    private static SessionStore? OpenStore()
    {
        try
        {
            var db = new Database(Path.Combine(AppConfigStore.GetDataDirectory(), "logagent.db"));
            db.EnsureCreated();
            return new SessionStore(db);
        }
        catch { return null; }
    }

    private void Load()
    {
        var store = OpenStore();
        if (store is null) return;
        Sessions.Clear();
        foreach (var row in store.ListSessions())
            Sessions.Add(new SessionRowItem(row));
    }

    private async Task CutSelectedAsync()
    {
        if (SelectedSession is not { IsActive: true } selected) return;
        var sessions = AppHost.Sessions;
        if (sessions is null)
        {
            // 服务未运行：直接按库切段（无摘要）
            var store = OpenStore();
            store?.CloseSession(selected.ChatId is { Length: > 0 } ? FindOpenSessionId(store, selected) ?? "" : "",
                DateTimeOffset.Now, "manual", null);
            Load();
            return;
        }
        await sessions.CutAsync(selected.ChatId, "manual");
        Load();
    }

    private static string? FindOpenSessionId(SessionStore? store, SessionRowItem selected)
    {
        // UI 场景兜底：按 chatId 找最近一个未关闭会话
        return store?.ListSessions(100)
            .FirstOrDefault(r => r.ChatId == selected.ChatId && r.EndTime is null)?.Id;
    }
}
