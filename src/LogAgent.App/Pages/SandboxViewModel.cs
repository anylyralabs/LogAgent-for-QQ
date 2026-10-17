using System.Collections.ObjectModel;
using System.IO;
using LogAgent.App.Infrastructure;
using LogAgent.App.Services;
using LogAgent.Sandbox;
using LogAgent.Shared.Configuration;

namespace LogAgent.App.Pages;

public sealed class AuditItem : ObservableObject
{
    public string Time { get; }
    public string Command { get; }
    public string Result { get; }
    public string Duration { get; }

    public AuditItem(CommandAuditEntry e)
    {
        Time = e.Ts.ToLocalTime().ToString("MM-dd HH:mm:ss");
        Command = e.Command.Length > 60 ? e.Command[..60] + "…" : e.Command;
        Result = !e.Allowed ? $"拒绝：{e.RejectReason}"
            : e.TimedOut ? "超时被杀"
            : e.ExitCode == 0 ? $"OK ({e.OutputBytes / 1024}KB)"
            : $"退出码 {e.ExitCode}";
        Duration = $"{e.Duration.TotalMilliseconds:F0}ms";
    }
}

public sealed class SandboxViewModel : ObservableObject
{
    public ObservableCollection<AuditItem> Audit { get; } = [];

    public string WhitelistText
    {
        get => _whitelist;
        set => Set(ref _whitelist, value);
    }

    public string BlacklistText
    {
        get => _blacklist;
        set => Set(ref _blacklist, value);
    }

    public RelayCommand SaveCommand { get; }

    private string _whitelist = "";
    private string _blacklist = "";

    public SandboxViewModel()
    {
        Load();
        SaveCommand = new RelayCommand(Save);
    }

    private void Load()
    {
        var config = AppConfigStore.Load().Sandbox;
        WhitelistText = string.Join(", ", config.WhitelistCommands);
        BlacklistText = string.Join("\n", config.BlacklistPatterns);
        RefreshAudit();
    }

    private void RefreshAudit()
    {
        Audit.Clear();
        // 服务未启动时无审计数据（内存态），启动后实时追加
        var service = AppHost.Sandbox;
        if (service is null) return;
        foreach (var entry in service.RecentAudit().OrderByDescending(e => e.Ts))
            Audit.Add(new AuditItem(entry));
        service.Audited += OnAudited;
    }

    private void OnAudited(object? sender, CommandAuditEntry e) =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            Audit.Insert(0, new AuditItem(e));
            if (Audit.Count > 200) Audit.RemoveAt(Audit.Count - 1);
        });

    private void Save()
    {
        var config = AppConfigStore.Load();
        config.Sandbox.WhitelistCommands = WhitelistText
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        config.Sandbox.BlacklistPatterns = BlacklistText
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        AppConfigStore.Save(config);
        System.Windows.MessageBox.Show("已保存（下次服务启动生效）", "沙箱配置",
            System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
    }
}
