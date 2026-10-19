using System.Collections.ObjectModel;
using LogAgent.Shared.Configuration;

namespace LogAgent.Desktop.ViewModels;

/// <summary>仪表盘：连接状态、模型调用计量（本次启动 + 今日累计）、会话与知识库统计。</summary>
public sealed class DashboardViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;

    public DashboardViewModel(ShellViewModel shell)
    {
        _shell = shell;
        RefreshCommand = new RelayCommand(Refresh);
        Refresh();
    }

    public ObservableCollection<StatRow> RouterRows { get; } = [];

    public string PlatformLine => _shell.PlatformLine;
    public string DataDirectory => _shell.DataDirectory;
    public string ConnectionState => _shell.ConnectionState;

    private string _modelCalls = "0";
    private string _todayUsage = "今日累计：暂无调用";
    private string _sessions = "—";
    private string _knowledge = "—";
    private string _providers = "—";
    private string _ftsState = "—";

    public string ModelCalls { get => _modelCalls; private set => Set(ref _modelCalls, value); }
    public string TodayUsage { get => _todayUsage; private set => Set(ref _todayUsage, value); }
    public string SessionCount { get => _sessions; private set => Set(ref _sessions, value); }
    public string KnowledgeCount { get => _knowledge; private set => Set(ref _knowledge, value); }
    public string ProviderSummary { get => _providers; private set => Set(ref _providers, value); }
    public string FtsState { get => _ftsState; private set => Set(ref _ftsState, value); }

    public RelayCommand RefreshCommand { get; }

    /// <summary>重新读取配置与数据库统计（页面激活 / 手动刷新时调用）。</summary>
    public void Refresh()
    {
        var host = _shell.Host;
        try
        {
            var stats = host.Stats.Snapshot();
            RouterRows.Clear();
            foreach (var s in stats)
                RouterRows.Add(new StatRow(s.Provider, s.Model, s.Ok, s.Fail,
                    s.PromptTokens + s.CompletionTokens, s.AvgLatencyMs));

            var (calls, ok, fail, pt, ct) = host.Usage.Today();
            ModelCalls = calls.ToString();
            TodayUsage = calls > 0
                ? $"今日累计：{calls} 次（成功 {ok} / 失败 {fail}）· tokens P {pt / 1000.0:F1}K + C {ct / 1000.0:F1}K"
                : "今日累计：暂无调用";
        }
        catch (Exception ex)
        {
            TodayUsage = $"用量统计不可用：{ex.Message}";
        }

        var config = AppConfigStore.Load();
        var enabled = config.Providers.Where(p => p.Enabled).ToList();
        ProviderSummary = enabled.Count == 0
            ? "尚未配置模型通道（去「模型路由」添加）"
            : $"{enabled.Count} 个启用：{string.Join("、", enabled.Select(p => p.Name))}";

        try
        {
            SessionCount = host.Sessions?.Store.CountSessions().ToString() ?? "—";
        }
        catch (Exception)
        {
            SessionCount = "—";
        }

        try
        {
            var approved = host.Knowledge?.List("approved").Count ?? 0;
            var candidates = host.Knowledge?.List("candidate").Count ?? 0;
            KnowledgeCount = $"{approved} 条已审 / {candidates} 条候选";
            FtsState = host.Knowledge?.IsFtsAvailable == true ? "FTS5 可用" : "FTS5 不可用（已降级为 LIKE）";
        }
        catch (Exception)
        {
            KnowledgeCount = "—";
        }

        Raise(nameof(ConnectionState));
    }
}

/// <summary>模型计量表格行。</summary>
public sealed record StatRow(string Provider, string Model, long Ok, long Fail, long TotalTokens, double AvgLatencyMs)
{
    public string SuccessRate => Ok + Fail == 0 ? "—" : $"{Ok * 100.0 / (Ok + Fail):F0}%";
    public string Latency => AvgLatencyMs <= 0 ? "—" : $"{AvgLatencyMs:F0} ms";
    public string Tokens => TotalTokens <= 0 ? "—" : TotalTokens >= 1000 ? $"{TotalTokens / 1000.0:F1}K" : TotalTokens.ToString();
}
