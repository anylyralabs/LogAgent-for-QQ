using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using LogAgent.App.Infrastructure;
using LogAgent.App.Services;
using LogAgent.Mcp;
using LogAgent.Shared.Configuration;
using LogAgent.Skills;

namespace LogAgent.App.Pages;

/// <summary>已安装技能条目（启用开关即时持久化）。</summary>
public sealed class SkillItemVM : ObservableObject
{
    public SkillRegistry.LoadedSkill Loaded { get; }
    public SkillManifest Manifest => Loaded.Manifest;

    public string Id => Manifest.Id;
    public string Name => Manifest.Name;
    public string Version => string.IsNullOrEmpty(Manifest.Version) ? "—" : "v" + Manifest.Version;
    public bool HasInstructions => !string.IsNullOrWhiteSpace(Manifest.Instructions);
    public string TypeDisplay => (Manifest.Type, HasInstructions) switch
    {
        ("http", true) => "HTTP+指令",
        ("http", false) => "HTTP 工具",
        ("prompt", _) => "指令知识",
        _ => Manifest.Type,
    };
    public string SourceDisplay => Loaded.Builtin ? "内置" : "用户";
    public string ToolDisplay => Manifest.Tools.Count > 0 ? $"{Manifest.Tools.Count} 个工具" : "—";
    public string Description => Manifest.Description;
    public bool CanUninstall => !Loaded.Builtin;

    private readonly Action<SkillItemVM, bool> _onToggle;
    private bool _enabled;

    public bool Enabled
    {
        get => _enabled;
        set { if (Set(ref _enabled, value)) _onToggle(this, value); }
    }

    public SkillItemVM(SkillRegistry.LoadedSkill loaded, bool enabled, Action<SkillItemVM, bool> onToggle)
    {
        Loaded = loaded;
        _enabled = enabled;
        _onToggle = onToggle;
    }
}

/// <summary>技能设置行（label + 可编辑值）。</summary>
public sealed class SkillSettingRow(SkillSettingSpec spec, string value) : ObservableObject
{
    public string Key { get; } = spec.Key;
    public string Label { get; } = string.IsNullOrEmpty(spec.Label) ? spec.Key : spec.Label;

    private string _value = value;
    public string Value { get => _value; set => Set(ref _value, value); }
}

/// <summary>商店条目。</summary>
public sealed class CatalogItemVM(CatalogEntry entry) : ObservableObject
{
    public CatalogEntry Entry { get; } = entry;
    public string Name => Entry.Name;
    public string VersionDisplay => string.IsNullOrEmpty(Entry.Version) ? "" : "v" + Entry.Version;
    public string Description => Entry.Description;
    public string Subtitle => string.Join(" · ", new[] { VersionDisplay, Entry.Author }.Where(s => !string.IsNullOrEmpty(s)));
}

/// <summary>MCP 服务器条目（配置 + 运行状态合成；启用开关即时持久化并触发同步）。</summary>
public sealed class McpServerItemVM : ObservableObject
{
    public McpServerEntry Entry { get; }
    private readonly McpServerStatus? _status;
    private readonly Action<McpServerItemVM, bool> _onToggle;
    private bool _enabled;

    public string Id => Entry.Id;
    public string Name => string.IsNullOrEmpty(Entry.Name) ? Entry.Id : Entry.Name;
    public string TransportDisplay => Entry.Transport == "http" ? "HTTP" : "stdio";
    public string Detail => Entry.Transport == "http"
        ? Entry.Url
        : $"{Entry.Command} {Entry.Args}".Trim();
    public bool IsReady => _status is { Ready: true };
    public string StateDisplay => !Entry.Enabled ? "已停用" : _status?.StateText ?? "未连接";
    public string ToolDisplay => IsReady ? $"{_status!.ToolCount} 个工具" : "—";
    public string? ErrorDisplay => _status?.Error;

    public bool Enabled
    {
        get => _enabled;
        set { if (Set(ref _enabled, value)) _onToggle(this, value); }
    }

    public McpServerItemVM(McpServerEntry entry, McpServerStatus? status, Action<McpServerItemVM, bool> onToggle)
    {
        Entry = entry;
        _status = status;
        _enabled = entry.Enabled;
        _onToggle = onToggle;
    }
}

/// <summary>内置 MCP 目录条目。</summary>
public sealed class McpCatalogItemVM(McpCatalogTemplate template)
{
    public McpCatalogTemplate Template { get; } = template;
    public string Name => Template.Name;
    public string Description => Template.Description;
    public string CommandHint => $"{Template.Command} {Template.Args}".Trim();
}

/// <summary>
/// 技能商店页：已安装技能（内置 + 用户）的查看/启停/卸载/配置 + 商店目录的拉取与安装。
/// 技能来源：内置目录（随安装包）与用户目录（%AppData%\LogAgent\skills）。
/// </summary>
public sealed class SkillsViewModel : ObservableObject
{
    public ObservableCollection<SkillItemVM> Installed { get; } = [];
    public ObservableCollection<CatalogItemVM> StoreItems { get; } = [];
    public ObservableCollection<SkillSettingRow> SettingRows { get; } = [];

    /// <summary>MCP 服务器（配置 + 状态）。</summary>
    public ObservableCollection<McpServerItemVM> McpServers { get; } = [];

    /// <summary>内置 MCP 目录（常用服务器模板）。</summary>
    public ObservableCollection<McpCatalogItemVM> McpCatalogItems { get; } = [];

    public SkillItemVM? Selected
    {
        get => _selected;
        set { if (Set(ref _selected, value)) RebuildSettingRows(); }
    }

    public McpServerItemVM? SelectedMcp
    {
        get => _selectedMcp;
        set => Set(ref _selectedMcp, value);
    }

    public McpCatalogItemVM? SelectedMcpCatalogItem
    {
        get => _selectedMcpCatalogItem;
        set => Set(ref _selectedMcpCatalogItem, value);
    }

    public CatalogItemVM? SelectedStoreItem
    {
        get => _selectedStoreItem;
        set => Set(ref _selectedStoreItem, value);
    }

    public string CatalogUrl { get => _catalogUrl; set => Set(ref _catalogUrl, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string StoreStatus { get => _storeStatus; private set => Set(ref _storeStatus, value); }
    public string McpStatus { get => _mcpStatus; set => Set(ref _mcpStatus, value); }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand UninstallCommand { get; }
    public RelayCommand OpenDirCommand { get; }
    public RelayCommand RefreshStoreCommand { get; }
    public RelayCommand InstallStoreItemCommand { get; }
    public RelayCommand SaveSettingsCommand { get; }

    private SkillItemVM? _selected;
    private McpServerItemVM? _selectedMcp;
    private McpCatalogItemVM? _selectedMcpCatalogItem;
    private CatalogItemVM? _selectedStoreItem;
    private string _catalogUrl = "";
    private string _status = "";
    private string _storeStatus = "";
    private string _mcpStatus = "";

    public SkillsViewModel()
    {
        if (AppHost.Skills is null)
        {
            try { AppHost.EnsureLocalChat(); } catch { /* 技能页独立打开时尽量初始化 */ }
        }
        _catalogUrl = AppHost.Config.Skills.CatalogUrl;
        RefreshCommand = new RelayCommand(ReloadSkills);
        UninstallCommand = new RelayCommand(UninstallSelected, () => Selected is { CanUninstall: true });
        OpenDirCommand = new RelayCommand(OpenUserDir);
        RefreshStoreCommand = new RelayCommand(() => _ = RefreshStoreAsync());
        InstallStoreItemCommand = new RelayCommand(() => _ = InstallStoreItemAsync(), () => SelectedStoreItem is not null);
        SaveSettingsCommand = new RelayCommand(SaveSettings, () => SettingRows.Count > 0);
        ReloadSkills();
        ReloadMcp();
        foreach (var template in McpCatalog.Builtin)
            McpCatalogItems.Add(new McpCatalogItemVM(template));
    }

    private static SkillRegistry? Registry => AppHost.Skills;

    private void ReloadSkills()
    {
        var registry = Registry;
        if (registry is null)
        {
            Status = "技能系统未就绪";
            return;
        }
        registry.Reload();
        var config = AppHost.Config.Skills;
        Installed.Clear();
        foreach (var skill in registry.Skills)
            Installed.Add(new SkillItemVM(skill, !config.DisabledSkills.Contains(skill.Manifest.Id), OnToggleSkill));
        Selected = Installed.FirstOrDefault();
        var builtin = Installed.Count(i => i.Loaded.Builtin);
        Status = $"已加载 {Installed.Count} 个技能（内置 {builtin} / 用户 {Installed.Count - builtin}）";
    }

    private void OnToggleSkill(SkillItemVM item, bool enabled)
    {
        var config = AppHost.Config.Skills;
        config.DisabledSkills.Remove(item.Id);
        if (!enabled) config.DisabledSkills.Add(item.Id);
        AppConfigStore.Save(AppHost.Config);
        Status = $"「{item.Name}」已{(enabled ? "启用" : "停用")}（对后续对话立即生效）";
    }

    private void RebuildSettingRows()
    {
        SettingRows.Clear();
        if (Selected is not { } item || item.Manifest.Settings.Count == 0) return;
        AppHost.Config.Skills.Settings.TryGetValue(item.Id, out var saved);
        foreach (var spec in item.Manifest.Settings)
        {
            var value = saved is not null && saved.TryGetValue(spec.Key, out var v) && !string.IsNullOrEmpty(v)
                ? v
                : spec.Default;
            SettingRows.Add(new SkillSettingRow(spec, value));
        }
    }

    private void SaveSettings()
    {
        if (Selected is not { } item || SettingRows.Count == 0) return;
        var map = AppHost.Config.Skills.Settings.TryGetValue(item.Id, out var existing)
            ? existing
            : AppHost.Config.Skills.Settings[item.Id] = new Dictionary<string, string>();
        foreach (var row in SettingRows) map[row.Key] = row.Value.Trim();
        AppConfigStore.Save(AppHost.Config);
        Status = $"「{item.Name}」设置已保存";
    }

    private void UninstallSelected()
    {
        if (Selected is not { } item) return;
        var installer = new SkillInstaller(UserDir());
        var result = installer.Uninstall(item.Id);
        StoreStatus = result;
        ReloadSkills();
    }

    private void OpenUserDir()
    {
        var dir = UserDir();
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
    }

    private async Task RefreshStoreAsync()
    {
        var config = AppHost.Config.Skills;
        config.CatalogUrl = CatalogUrl.Trim();
        AppConfigStore.Save(AppHost.Config);
        if (config.CatalogUrl.Length == 0)
        {
            StoreStatus = "未配置商店地址（仍可从文件/文件夹安装本地技能包）";
            return;
        }
        if (!config.CatalogUrl.Contains("://", StringComparison.Ordinal))
        {
            StoreStatus = "商店地址须是完整 URL（http/https）";
            return;
        }
        try
        {
            StoreStatus = "⏳ 正在拉取商店目录…";
            var entries = await SkillCatalog.FetchAsync(config.CatalogUrl);
            StoreItems.Clear();
            foreach (var entry in entries) StoreItems.Add(new CatalogItemVM(entry));
            StoreStatus = $"✅ 商店共 {entries.Count} 个技能{(entries.Count == 0 ? "（目录为空）" : "")}";
        }
        catch (Exception ex)
        {
            StoreStatus = $"❌ 商店拉取失败：{ex.Message}";
        }
    }

    private async Task InstallStoreItemAsync()
    {
        if (SelectedStoreItem is not { } item) return;
        if (string.IsNullOrWhiteSpace(item.Entry.DownloadUrl))
        {
            StoreStatus = "该条目没有提供下载地址";
            return;
        }
        var installer = new SkillInstaller(UserDir());
        StoreStatus = $"⏳ 正在安装 {item.Name}…";
        StoreStatus = await installer.InstallFromZipAsync(item.Entry.DownloadUrl);
        ReloadSkills();
    }

    /// <summary>从本地 zip 或目录安装（页面按钮调用）。</summary>
    public async Task InstallFromPathAsync(string path)
    {
        var installer = new SkillInstaller(UserDir());
        StoreStatus = "⏳ 正在安装…";
        StoreStatus = Directory.Exists(path)
            ? installer.InstallFromFolder(path)
            : await installer.InstallFromZipAsync(path);
        ReloadSkills();
    }

    private static string UserDir()
        => Registry?.UserDir ?? Path.Combine(AppConfigStore.GetConfigDirectory(), "skills");

    // ─────────────────────────── MCP 服务器 ───────────────────────────

    /// <summary>重建 MCP 列表（配置 + 实时状态快照）。</summary>
    public void ReloadMcp()
    {
        var statuses = AppHost.Mcp?.GetStatus().ToDictionary(s => s.Id) ?? [];
        McpServers.Clear();
        foreach (var entry in AppHost.Config.Mcp.Servers)
            McpServers.Add(new McpServerItemVM(entry, statuses.GetValueOrDefault(entry.Id), OnToggleMcp));
        var ready = McpServers.Count(m => m.IsReady);
        var enabled = McpServers.Count(m => m.Enabled);
        McpStatus = McpServers.Count == 0
            ? "尚未配置 MCP 服务器（可从右侧目录一键添加，或点「添加服务器…」）"
            : $"MCP：{ready}/{enabled} 台在线 · 共 {McpServers.Count} 台配置";
    }

    private void OnToggleMcp(McpServerItemVM item, bool enabled)
    {
        item.Entry.Enabled = enabled;
        AppConfigStore.Save(AppHost.Config);
        McpStatus = $"「{item.Name}」已{(enabled ? "启用" : "停用")}，正在同步连接…";
        _ = SyncAndReloadMcpAsync();
    }

    /// <summary>添加 MCP 服务器（弹窗保存后调用）。</summary>
    public async Task AddMcpEntryAsync(McpServerEntry entry)
    {
        AppHost.Config.Mcp.Servers.Add(entry);
        AppConfigStore.Save(AppHost.Config);
        McpStatus = $"「{entry.Name}」已添加，正在连接…";
        await SyncAndReloadMcpAsync();
    }

    /// <summary>更新 MCP 服务器（按 Id 替换；不存在则追加）。</summary>
    public async Task UpdateMcpEntryAsync(McpServerEntry entry)
    {
        var list = AppHost.Config.Mcp.Servers;
        var index = list.FindIndex(s => s.Id == entry.Id);
        if (index >= 0) list[index] = entry; else list.Add(entry);
        AppConfigStore.Save(AppHost.Config);
        McpStatus = $"「{entry.Name}」已保存，正在重连…";
        await SyncAndReloadMcpAsync();
    }

    /// <summary>删除选中的 MCP 服务器。</summary>
    public async Task RemoveSelectedMcpAsync()
    {
        if (SelectedMcp is not { } item)
        {
            McpStatus = "先选中要删除的服务器";
            return;
        }
        AppHost.Config.Mcp.Servers.RemoveAll(s => s.Id == item.Id);
        AppConfigStore.Save(AppHost.Config);
        McpStatus = $"「{item.Name}」已删除";
        await SyncAndReloadMcpAsync();
    }

    /// <summary>重连所有 MCP 服务器。</summary>
    public async Task ReconnectMcpAsync()
    {
        McpStatus = "正在同步 MCP 连接…";
        await SyncAndReloadMcpAsync();
    }

    private async Task SyncAndReloadMcpAsync()
    {
        try
        {
            await AppHost.SyncMcpAsync();
        }
        catch (Exception ex)
        {
            McpStatus = $"同步失败：{ex.Message}";
        }
        ReloadMcp();
    }
}
