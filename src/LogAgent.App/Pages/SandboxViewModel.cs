using System.Collections.ObjectModel;
using LogAgent.App.Infrastructure;
using LogAgent.App.Services;
using LogAgent.Sandbox;
using LogAgent.Shared.Configuration;
using LogAgent.Shared.Platform;

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
    private string _saveHint = "";

    /// <summary>当前平台的 shell 说明（沙箱页提示）。</summary>
    public string ShellInfo => $"当前平台：{PlatformShell.Current.DisplayName}（默认命令集按平台生成）";

    /// <summary>旧配置里的 Windows 命令（Unix 上提示需要重置）。</summary>
    public string LegacyHint { get; private set; } = "";

    public bool HasLegacyHint => LegacyHint.Length > 0;

    public RelayCommand ResetToDefaultsCommand { get; }

    /// <summary>保存/重置后的提示（沙箱页顶部显示）。</summary>
    public string SaveHint { get => _saveHint; private set => Set(ref _saveHint, value); }

    public SandboxViewModel()
    {
        Load();
        SaveCommand = new RelayCommand(Save);
        ResetToDefaultsCommand = new RelayCommand(ResetToDefaults);
    }

    private void Load()
    {
        var config = AppConfigStore.Load().Sandbox;
        WhitelistText = string.Join(", ", config.WhitelistCommands);
        BlacklistText = string.Join("\n", config.BlacklistPatterns);

        var legacy = config.WindowsOnlyCommandsInWhitelist();
        LegacyHint = legacy.Count == 0
            ? ""
            : $"白名单含 Windows 专有命令（{string.Join(", ", legacy)}），当前平台执行会被拒绝；可点「恢复平台默认」重置。";
        Raise(nameof(LegacyHint));
        Raise(nameof(HasLegacyHint));
        RefreshAudit();
    }

    /// <summary>把白名单/黑名单恢复为当前平台默认（仅改界面内容，点保存才落盘）。</summary>
    private void ResetToDefaults()
    {
        var defaults = new SandboxConfig();
        WhitelistText = string.Join(", ", defaults.WhitelistCommands);
        BlacklistText = string.Join("\n", defaults.BlacklistPatterns);
        LegacyHint = "";
        SaveHint = "已填入当前平台默认值（点「保存配置」生效）";
        Raise(nameof(LegacyHint));
        Raise(nameof(HasLegacyHint));
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
        SaveHint = $"已保存（{DateTime.Now:HH:mm:ss} 生效于下次服务启动）";
        System.Windows.MessageBox.Show("已保存（下次服务启动生效）", "沙箱配置",
            System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
    }
}
