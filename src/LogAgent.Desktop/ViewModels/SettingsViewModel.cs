using LogAgent.Shared.Configuration;
using LogAgent.Shared.Platform;

namespace LogAgent.Desktop.ViewModels;

/// <summary>设置页：触发规则 / 会话参数 / 安全沙箱 / 启动选项（读写 config.json）。</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private string _saveHint = "";
    private bool _needAt;
    private string _keywords = "";
    private string _idle = "30";
    private string _maxTokens = "8000";
    private string _maxMessages = "200";
    private bool _summaryOnCut = true;
    private bool _sandboxEnabled = true;
    private string _sandboxTimeout = "30";
    private bool _autoStart;
    private bool _autoStartBots;
    private bool _startMinimized;

    public SettingsViewModel(ShellViewModel shell)
    {
        _shell = shell;
        SaveCommand = new RelayCommand(Save);
        OpenDataDirCommand = new RelayCommand(OpenDataDir);
        RepairSandboxCommand = new RelayCommand(RepairSandbox);
        AutoStartLabel = Services.PlatformService.AutoStartLabel;
        Load();
    }

    public string AutoStartLabel { get; }
    public string PlatformLine => _shell.PlatformLine;
    public string DataDirectory => AppConfigStore.GetConfigDirectory();
    public string ConfigPath => AppConfigStore.GetConfigPath();
    public string ShellDisplay => PlatformShell.Current.DisplayName;
    public string WhitelistPreview => string.Join(", ", PlatformShell.Current.DefaultWhitelist);

    /// <summary>沙箱平台说明（整句，供视图直接绑定；避免在 Run 上做绑定）。</summary>
    public string SandboxPlatformHint =>
        $"当前平台 shell：{PlatformShell.Current.DisplayName}；默认白名单：{WhitelistPreview}";

    public bool NeedAtInGroup { get => _needAt; set => Set(ref _needAt, value); }
    public string TriggerKeywords { get => _keywords; set => Set(ref _keywords, value); }
    public string IdleTimeoutMinutes { get => _idle; set => Set(ref _idle, value); }
    public string SessionMaxTokens { get => _maxTokens; set => Set(ref _maxTokens, value); }
    public string SessionMaxMessages { get => _maxMessages; set => Set(ref _maxMessages, value); }
    public bool SummaryOnCut { get => _summaryOnCut; set => Set(ref _summaryOnCut, value); }
    public bool SandboxEnabled { get => _sandboxEnabled; set => Set(ref _sandboxEnabled, value); }
    public string SandboxTimeoutSeconds { get => _sandboxTimeout; set => Set(ref _sandboxTimeout, value); }
    public bool AutoStartBots { get => _autoStartBots; set => Set(ref _autoStartBots, value); }
    public bool StartMinimized { get => _startMinimized; set => Set(ref _startMinimized, value); }

    public bool AutoStart
    {
        get => _autoStart;
        set
        {
            if (!Set(ref _autoStart, value)) return;
            var ok = Services.PlatformService.SetAutoStart(value);
            SaveHint = ok ? (value ? "开机自启动已开启" : "开机自启动已关闭") : "开机自启动设置失败（权限不足？）";
        }
    }

    public string SaveHint { get => _saveHint; private set => Set(ref _saveHint, value); }

    public RelayCommand SaveCommand { get; }
    public RelayCommand OpenDataDirCommand { get; }
    public RelayCommand RepairSandboxCommand { get; }

    private void Load()
    {
        var config = AppConfigStore.Load();
        _needAt = config.Agent.NeedAtInGroup;
        _keywords = string.Join(", ", config.Agent.TriggerKeywords);
        _idle = config.Agent.SessionIdleTimeoutMinutes.ToString();
        _maxTokens = config.Agent.SessionMaxTokens.ToString();
        _maxMessages = config.Agent.SessionMaxMessages.ToString();
        _summaryOnCut = config.Agent.SummaryOnCut;
        _sandboxEnabled = config.Sandbox.Enabled;
        _sandboxTimeout = config.Sandbox.CommandTimeoutSeconds.ToString();
        _autoStartBots = config.AutoStartBots;
        _startMinimized = config.StartMinimized;
        _autoStart = Services.PlatformService.IsAutoStartEnabled();

        Raise(nameof(NeedAtInGroup));
        Raise(nameof(TriggerKeywords));
        Raise(nameof(IdleTimeoutMinutes));
        Raise(nameof(SessionMaxTokens));
        Raise(nameof(SessionMaxMessages));
        Raise(nameof(SummaryOnCut));
        Raise(nameof(SandboxEnabled));
        Raise(nameof(SandboxTimeoutSeconds));
        Raise(nameof(AutoStartBots));
        Raise(nameof(StartMinimized));
        Raise(nameof(AutoStart));
    }

    private void Save()
    {
        if (!TryParseInt(IdleTimeoutMinutes, 1, 1440, out var idle) ||
            !TryParseInt(SessionMaxTokens, 500, 200000, out var maxTokens) ||
            !TryParseInt(SessionMaxMessages, 10, 5000, out var maxMessages))
        {
            SaveHint = "保存失败：会话参数需为数字（空闲分钟 1-1440 / token 500-200000 / 消息数 10-5000）";
            return;
        }
        if (!TryParseInt(SandboxTimeoutSeconds, 5, 600, out var sandboxTimeout))
        {
            SaveHint = "保存失败：命令超时需为 5-600 之间的数字";
            return;
        }

        var config = AppConfigStore.Load();
        config.Agent.NeedAtInGroup = NeedAtInGroup;
        config.Agent.TriggerKeywords = TriggerKeywords
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        config.Agent.SessionIdleTimeoutMinutes = idle;
        config.Agent.SessionMaxTokens = maxTokens;
        config.Agent.SessionMaxMessages = maxMessages;
        config.Agent.SummaryOnCut = SummaryOnCut;
        config.Sandbox.Enabled = SandboxEnabled;
        config.Sandbox.CommandTimeoutSeconds = sandboxTimeout;
        config.AutoStartBots = AutoStartBots;
        config.StartMinimized = StartMinimized;

        AppConfigStore.Save(config);
        SaveHint = "已保存 ✅（会话 / 触发参数在服务重启后生效）";
    }

    /// <summary>解析并校验整数输入；超出范围按边界收敛（与 WPF 壳的 Math.Clamp 行为一致）。</summary>
    private static bool TryParseInt(string raw, int min, int max, out int value)
    {
        if (!int.TryParse(raw?.Trim(), out var parsed))
        {
            value = 0;
            return false;
        }
        value = Math.Clamp(parsed, min, max);
        return true;
    }

    /// <summary>把沙箱命令规则重置为当前平台默认（先备份配置）。</summary>
    private void RepairSandbox()
    {
        var config = AppConfigStore.Load();
        var backup = AppConfigStore.Backup();
        config.Sandbox.ResetCommandRulesToPlatformDefaults();
        AppConfigStore.Save(config);
        SaveHint = backup is null
            ? "沙箱命令规则已重置为平台默认（原配置不存在，未备份）"
            : $"沙箱命令规则已重置为平台默认；备份：{Path.GetFileName(backup)}";
    }

    private void OpenDataDir()
    {
        var dir = AppConfigStore.GetConfigDirectory();
        Directory.CreateDirectory(dir);
        var error = Services.PlatformService.Open(dir);
        SaveHint = error is null ? $"已打开：{dir}" : $"打开目录失败：{error}";
    }
}
