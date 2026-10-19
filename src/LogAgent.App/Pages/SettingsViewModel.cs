using System.IO;
using LogAgent.App.Infrastructure;
using LogAgent.App.Services;
using LogAgent.Shared.Configuration;

namespace LogAgent.App.Pages;

public sealed class SettingsViewModel : ObservableObject
{
    // 触发规则
    public bool NeedAtInGroup { get => _needAt; set => Set(ref _needAt, value); }
    public string TriggerKeywords { get => _keywords; set => Set(ref _keywords, value); }

    // 会话切断
    public int IdleTimeoutMinutes { get => _idle; set => Set(ref _idle, value); }
    public int MaxTokens { get => _maxTokens; set => Set(ref _maxTokens, value); }
    public int MaxMessages { get => _maxMsgs; set => Set(ref _maxMsgs, value); }
    public bool SummaryOnCut { get => _summary; set => Set(ref _summary, value); }

    // 知识库
    public bool KnowledgeEnabled { get => _kbEnabled; set => Set(ref _kbEnabled, value); }
    public int RagTopK { get => _ragTopK; set => Set(ref _ragTopK, value); }
    public bool KnowledgeAutoApprove { get => _kbAutoApprove; set => Set(ref _kbAutoApprove, value); }

    // 沙箱
    public bool SandboxEnabled { get => _sbEnabled; set => Set(ref _sbEnabled, value); }
    public int SandboxTimeout { get => _sbTimeout; set => Set(ref _sbTimeout, value); }
    public int SandboxOutputKb { get => _sbOutputKb; set => Set(ref _sbOutputKb, value); }
    public int SandboxDownloadMb { get => _sbDownloadMb; set => Set(ref _sbDownloadMb, value); }

    // 启动与自动化
    public bool AutoLaunchWindows
    {
        get => _autoLaunch;
        set
        {
            if (!Set(ref _autoLaunch, value)) return;
            // 开机自启动立即生效（Windows 注册表 HKCU Run / Linux ~/.config/autostart）
            AutoStartManager.SetEnabled(value);
            SaveHint = value ? "开机自启动已开启" : "开机自启动已关闭";
        }
    }

    /// <summary>开机自启动复选框文案（按平台给出准确描述）。</summary>
    public string AutoLaunchLabel => AutoStartManager.Describe();

    public bool AutoStartBots { get => _autoStartBots; set => Set(ref _autoStartBots, value); }
    public bool StartMinimized { get => _startMinimized; set => Set(ref _startMinimized, value); }

    // 数据与维护
    public string DataDirInfo { get => _dataDirInfo; private set => Set(ref _dataDirInfo, value); }
    public string SaveHint { get => _saveHint; private set => Set(ref _saveHint, value); }

    public RelayCommand SaveCommand { get; }
    public RelayCommand OpenDataDirCommand { get; }

    private bool _needAt;
    private string _keywords = "";
    private int _idle;
    private int _maxTokens;
    private int _maxMsgs;
    private bool _summary;
    private bool _kbEnabled;
    private int _ragTopK;
    private bool _kbAutoApprove;
    private bool _sbEnabled;
    private int _sbTimeout;
    private int _sbOutputKb;
    private int _sbDownloadMb;
    private bool _autoLaunch;
    private bool _autoStartBots;
    private bool _startMinimized;
    private string _dataDirInfo = "—";
    private string _saveHint = "";

    public SettingsViewModel()
    {
        var config = AppConfigStore.Load();

        _needAt = config.Agent.NeedAtInGroup;
        _keywords = string.Join(", ", config.Agent.TriggerKeywords);
        _idle = config.Agent.SessionIdleTimeoutMinutes;
        _maxTokens = config.Agent.SessionMaxTokens;
        _maxMsgs = config.Agent.SessionMaxMessages;
        _summary = config.Agent.SummaryOnCut;

        _kbEnabled = config.Knowledge.Enabled;
        _ragTopK = config.Knowledge.RagTopK;
        _kbAutoApprove = config.Knowledge.AutoApprove;

        _sbEnabled = config.Sandbox.Enabled;
        _sbTimeout = config.Sandbox.CommandTimeoutSeconds;
        _sbOutputKb = config.Sandbox.MaxOutputKb;
        _sbDownloadMb = config.Sandbox.MaxDownloadMb;

        _autoLaunch = AutoStartManager.IsEnabled();
        _autoStartBots = config.AutoStartBots;
        _startMinimized = config.StartMinimized;

        SaveCommand = new RelayCommand(Save);
        OpenDataDirCommand = new RelayCommand(OpenDataDir);
        RefreshDataInfo();
    }

    private void Save()
    {
        var config = AppConfigStore.Load();

        // 就地更新（避免整对象替换丢字段）
        config.Agent.NeedAtInGroup = NeedAtInGroup;
        config.Agent.TriggerKeywords = TriggerKeywords
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        config.Agent.SessionIdleTimeoutMinutes = Math.Max(1, IdleTimeoutMinutes);
        config.Agent.SessionMaxTokens = Math.Max(500, MaxTokens);
        config.Agent.SessionMaxMessages = Math.Max(10, MaxMessages);
        config.Agent.SummaryOnCut = SummaryOnCut;

        config.Knowledge.Enabled = KnowledgeEnabled;
        config.Knowledge.RagTopK = Math.Clamp(RagTopK, 1, 10);
        config.Knowledge.AutoApprove = KnowledgeAutoApprove;

        config.Sandbox.Enabled = SandboxEnabled;
        config.Sandbox.CommandTimeoutSeconds = Math.Clamp(SandboxTimeout, 5, 600);
        config.Sandbox.MaxOutputKb = Math.Clamp(SandboxOutputKb, 4, 1024);
        config.Sandbox.MaxDownloadMb = Math.Clamp(SandboxDownloadMb, 1, 2048);

        config.AutoStartBots = AutoStartBots;
        config.StartMinimized = StartMinimized;

        AppConfigStore.Save(config);
        SaveHint = "已保存 ✅　会话 / 触发参数在 Bot 服务下次启动后生效";
        RefreshDataInfo();
    }

    private void RefreshDataInfo()
    {
        var dataDir = AppConfigStore.GetDataDirectory();
        try
        {
            long total = 0;
            foreach (var file in Directory.EnumerateFiles(dataDir, "*.db"))
                total += new FileInfo(file).Length;
            DataDirInfo = $"数据目录：{dataDir}　（数据库共 {total / 1024.0 / 1024.0:F1} MB）";
        }
        catch
        {
            DataDirInfo = "数据目录尚未创建（首次运行后自动生成）";
        }
    }

    private void OpenDataDir()
    {
        var dataDir = AppConfigStore.GetDataDirectory();
        Directory.CreateDirectory(dataDir);
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dataDir)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            SaveHint = $"打开目录失败：{ex.Message}";
        }
    }
}
