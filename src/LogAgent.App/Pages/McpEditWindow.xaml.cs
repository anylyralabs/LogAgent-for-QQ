using System.Windows;
using System.Windows.Controls;
using LogAgent.Mcp;
using LogAgent.Shared.Configuration;

namespace LogAgent.App.Pages;

/// <summary>MCP 服务器编辑弹窗：名称/传输方式/命令参数/环境变量/URL 请求头 + 就地测试连接。</summary>
public partial class McpEditWindow : Window
{
    private readonly string _id;

    /// <summary>保存后的配置结果。</summary>
    public McpServerEntry? Result { get; private set; }

    public McpEditWindow(McpServerEntry? existing = null)
    {
        InitializeComponent();
        if (existing is null)
        {
            _id = Guid.NewGuid().ToString("N")[..8];
            Title = "LogAgent — 添加 MCP 服务器";
            HeaderText.Text = "添加 MCP 服务器";
            TransportCombo.SelectedIndex = 0;
        }
        else
        {
            _id = string.IsNullOrEmpty(existing.Id) ? Guid.NewGuid().ToString("N")[..8] : existing.Id;
            Title = "LogAgent — 编辑 MCP 服务器";
            HeaderText.Text = "编辑 MCP 服务器";
            NameBox.Text = existing.Name;
            TransportCombo.SelectedIndex = existing.Transport == "http" ? 1 : 0;
            CommandBox.Text = existing.Command;
            ArgsBox.Text = existing.Args;
            WorkdirBox.Text = existing.WorkingDirectory;
            EnvBox.Text = FormatPairs(existing.Env, "=");
            UrlBox.Text = existing.Url;
            HeadersBox.Text = FormatPairs(existing.Headers, ": ");
            EnabledToggle.IsChecked = existing.Enabled;
        }
        UpdateGroupVisibility();
        Loaded += (_, _) => NameBox.Focus();
    }

    private void Transport_Changed(object sender, SelectionChangedEventArgs e) => UpdateGroupVisibility();

    private void UpdateGroupVisibility()
    {
        var isHttp = TransportCombo.SelectedIndex == 1;
        StdioGroup.Visibility = isHttp ? Visibility.Collapsed : Visibility.Visible;
        HttpGroup.Visibility = isHttp ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string FormatPairs(Dictionary<string, string> map, string separator)
        => string.Join(Environment.NewLine, map.Select(kv => $"{kv.Key}{separator}{kv.Value}"));

    /// <summary>解析多行 KEY=VALUE / Key: Value 文本为字典（坏行跳过）。</summary>
    private static Dictionary<string, string> ParsePairs(string text, char[] separators)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var idx = line.IndexOfAny(separators);
            if (idx <= 0) continue;
            var key = line[..idx].Trim().TrimEnd(':');
            var value = line[(idx + 1)..].Trim();
            if (key.Length > 0) map[key] = value;
        }
        return map;
    }

    /// <summary>从表单读取配置；校验失败就地提示并返回 null。</summary>
    private McpServerEntry? ReadForm(bool requireName, out bool isHttp)
    {
        isHttp = TransportCombo.SelectedIndex == 1;
        var name = NameBox.Text.Trim();
        if (requireName && name.Length == 0)
        {
            SetStatus("请填写名称", ok: false);
            NameBox.Focus();
            return null;
        }

        if (isHttp)
        {
            var url = UrlBox.Text.Trim();
            if (url.Length == 0 || !url.Contains("://", StringComparison.Ordinal))
            {
                SetStatus("请填写合法的 URL（含 http:// 或 https://）", ok: false);
                UrlBox.Focus();
                return null;
            }
        }
        else if (CommandBox.Text.Trim().Length == 0)
        {
            SetStatus("请填写命令（如 npx / uvx / python 或完整 exe 路径）", ok: false);
            CommandBox.Focus();
            return null;
        }

        return new McpServerEntry
        {
            Id = _id,
            Name = name,
            Transport = isHttp ? "http" : "stdio",
            Command = CommandBox.Text.Trim(),
            Args = ArgsBox.Text.Trim(),
            WorkingDirectory = WorkdirBox.Text.Trim(),
            Env = ParsePairs(EnvBox.Text, ['=', ':']),
            Url = UrlBox.Text.Trim(),
            Headers = ParsePairs(HeadersBox.Text, [':', '=']),
            Enabled = EnabledToggle.IsChecked == true,
        };
    }

    /// <summary>测试连接：以当前表单参数建立会话并列举工具（不保存）。</summary>
    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        var entry = ReadForm(requireName: false, out _);
        if (entry is null) return;

        TestBtn.IsEnabled = false;
        SetStatus("⏳ 正在连接并握手（stdio 首次启动可能较慢）…", ok: null);
        try
        {
            await using var client = new McpClient(entry);
            await client.ConnectAsync(CancellationToken.None);
            if (client.State == McpClientState.Ready)
            {
                var tools = client.Tools.Count == 0
                    ? "（该服务器未提供工具）"
                    : string.Join("、", client.Tools.Take(8).Select(t => t.Name))
                      + (client.Tools.Count > 8 ? $" 等 {client.Tools.Count} 个" : "");
                SetStatus($"✅ 连接成功 · {client.Server?.Name} v{client.Server?.Version} · 协议 {client.ProtocolVersion}\n工具：{tools}",
                    ok: true);
            }
            else
            {
                SetStatus($"❌ 连接失败：{client.LastError}", ok: false);
            }
        }
        catch (Exception ex)
        {
            SetStatus($"❌ 连接失败：{ex.Message}", ok: false);
        }
        finally
        {
            TestBtn.IsEnabled = true;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var entry = ReadForm(requireName: true, out _);
        if (entry is null) return;
        if (entry.Name.Length == 0) entry.Name = entry.Id;
        Result = entry;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void SetStatus(string text, bool? ok)
    {
        StatusText.Text = text;
        StatusText.Visibility = Visibility.Visible;
        StatusText.Foreground = (System.Windows.Media.Brush)FindResource(
            ok switch { true => "OkFill", false => "ErrorFill", null => "TextSecondary" });
    }
}
