using System.Windows;
using System.Windows.Controls;
using LogAgent.Mcp;
using LogAgent.Shared.Configuration;

namespace LogAgent.App.Pages;

public partial class SkillsPage : Page
{
    private readonly SkillsViewModel _vm = new();

    public SkillsPage()
    {
        InitializeComponent();
        DataContext = _vm;
    }

    /// <summary>从本地 zip 安装技能包。</summary>
    private async void InstallFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择技能包（zip）",
            Filter = "技能包 (*.zip)|*.zip|所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true) return;
        await _vm.InstallFromPathAsync(dialog.FileName);
    }

    /// <summary>从本地目录安装技能包（目录内须有 skill.json 或 SKILL.md）。</summary>
    private async void InstallFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择技能包目录（内含 skill.json 或 SKILL.md）",
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        await _vm.InstallFromPathAsync(dialog.SelectedPath);
    }

    // ─────────────────────────── MCP 服务器 ───────────────────────────

    private void AddMcp_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new McpEditWindow { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true && dialog.Result is { } entry)
            _ = _vm.AddMcpEntryAsync(entry);
    }

    private void EditMcp_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedMcp is not { } item)
        {
            _vm.McpStatus = "先选中要编辑的 MCP 服务器";
            return;
        }
        var dialog = new McpEditWindow(item.Entry) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true && dialog.Result is { } entry)
            _ = _vm.UpdateMcpEntryAsync(entry);
    }

    private void RemoveMcp_Click(object sender, RoutedEventArgs e) => _ = _vm.RemoveSelectedMcpAsync();

    private void ReconnectMcp_Click(object sender, RoutedEventArgs e) => _ = _vm.ReconnectMcpAsync();

    /// <summary>从内置目录添加：预填模板到编辑弹窗，确认后加入配置。</summary>
    private void AddMcpFromCatalog_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedMcpCatalogItem is not { } item)
        {
            _vm.McpStatus = "先选中一个服务器模板";
            return;
        }
        var template = item.Template;
        var entry = new McpServerEntry
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = template.Name,
            Transport = template.Transport,
            Command = template.Command,
            Args = template.Args,
        };
        if (!string.IsNullOrWhiteSpace(template.EnvHint))
        {
            foreach (var raw in template.EnvHint.Split(';'))
            {
                var idx = raw.IndexOf('=');
                if (idx > 0) entry.Env[raw[..idx].Trim()] = raw[(idx + 1)..].Trim();
            }
        }

        var dialog = new McpEditWindow(entry) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true && dialog.Result is { } result)
            _ = _vm.AddMcpEntryAsync(result);
    }
}
