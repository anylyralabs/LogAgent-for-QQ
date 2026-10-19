using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media.Imaging;
using LogAgent.App.Infrastructure;
using LogAgent.App.Services;
using LogAgent.NapCat;
using LogAgent.Shared.Configuration;
using QRCoder;

namespace LogAgent.App.Pages;

public sealed class NapCatViewModel : ObservableObject
{
    public ObservableCollection<string> Logs { get; } = [];

    public string NapCatScript
    {
        get => _napCatScript;
        set => Set(ref _napCatScript, value);
    }

    public string WebUiToken
    {
        get => _webUiToken;
        set => Set(ref _webUiToken, value);
    }

    public int WebUiPort { get => _webUiPort; set => Set(ref _webUiPort, value); }

    public int OneBotPort
    {
        get => _oneBotPort;
        set { if (Set(ref _oneBotPort, value)) Raise(nameof(WsAddress)); }
    }
    public string OneBotToken { get => _oneBotToken; set => Set(ref _oneBotToken, value); }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public string NapCatStatus
    {
        get => _napCatStatus;
        private set => Set(ref _napCatStatus, value);
    }

    public BitmapImage? QrImage
    {
        get => _qrImage;
        private set => Set(ref _qrImage, value);
    }

    public bool ServiceRunning
    {
        get => _serviceRunning;
        private set { if (Set(ref _serviceRunning, value)) Raise(nameof(ServiceStatusText)); }
    }

    public RelayCommand SaveConfigCommand { get; }
    public RelayCommand StartNapCatCommand { get; }
    public RelayCommand StopNapCatCommand { get; }
    public RelayCommand RefreshQrCommand { get; }
    public RelayCommand StartServiceCommand { get; }
    public RelayCommand StopServiceCommand { get; }
    public RelayCommand QuickLoginCommand { get; }

    public string QuickLoginUin { get => _quickLoginUin; set => Set(ref _quickLoginUin, value); }

    /// <summary>OneBot 服务状态文字。</summary>
    public string ServiceStatusText => ServiceRunning ? "运行中" : "未启动";

    /// <summary>反向 WS 地址（供 NapCat 网络配置填写）。</summary>
    public string WsAddress => $"ws://127.0.0.1:{OneBotPort}/onebot/v11/ws";

    public RelayCommand BrowseScriptCommand { get; }
    public RelayCommand InjectConfigCommand { get; }

    private string _napCatScript = "";
    private string _webUiToken = "";
    private int _webUiPort = 6099;
    private int _oneBotPort = 6701;
    private string _oneBotToken = "";
    private string _statusText = "未登录";
    private string _napCatStatus = "未检测";
    private BitmapImage? _qrImage;
    private bool _serviceRunning;
    private string _quickLoginUin = "";
    private NapCatManager? _manager;
    private NapCatWebUiClient? _webUi;
    private System.Windows.Threading.DispatcherTimer? _timer;
    private System.Windows.Threading.DispatcherTimer? _statusTimer;

    public NapCatViewModel()
    {
        SaveConfigCommand = new RelayCommand(SaveConfig);
        StartNapCatCommand = new RelayCommand(() => _ = StartNapCatAsync());
        StopNapCatCommand = new RelayCommand(() => _ = StopNapCatAsync());
        RefreshQrCommand = new RelayCommand(() => _ = RefreshQrAsync());
        StartServiceCommand = new RelayCommand(() => _ = StartServiceAsync());
        StopServiceCommand = new RelayCommand(() => _ = StopServiceAsync());
        QuickLoginCommand = new RelayCommand(() => _ = QuickLoginAsync(), () => QuickLoginUin.Length > 0);
        BrowseScriptCommand = new RelayCommand(BrowseScript);
        InjectConfigCommand = new RelayCommand(InjectConfig);

        LoadConfig();

        // 状态定时刷新：NapCat 进程 / OneBot 服务（从任何入口变化都能反映）
        _statusTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _statusTimer.Tick += (_, _) =>
        {
            RefreshNapCatStatus();
            ServiceRunning = AppHost.OneBotServerStarted;
        };
        _statusTimer.Start();

        AppHost.Logged += line => System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            Logs.Add(line);
            if (Logs.Count > 300) Logs.RemoveAt(0);
        });
    }

    /// <summary>页面卸载时停掉定时器（防止后台常驻）。</summary>
    public void StopTimers()
    {
        _statusTimer?.Stop();
        _timer?.Stop();
    }

    private void BrowseScript()
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择 NapCat 启动脚本",
                // 脚本类型随平台变化：Windows 用 .bat/.cmd/.exe，Linux 用 .sh / 无扩展名可执行脚本
                Filter = OperatingSystem.IsWindows()
                    ? "启动脚本 (*.bat;*.cmd;*.exe)|*.bat;*.cmd;*.exe|所有文件 (*.*)|*.*"
                    : "启动脚本 (*.sh;可执行文件)|*.sh|所有文件 (*.*)|*.*",
            };
            // 带 Owner 弹出，确保对话框显示在主窗口之上
            var owner = System.Windows.Application.Current?.MainWindow;
            var ok = owner is not null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
            if (ok == true)
            {
                NapCatScript = dialog.FileName;
                SaveConfig();
                Log($"已选择启动脚本：{dialog.FileName}");
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"选择脚本失败：{ex.Message}", "OneBot 连接",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    private void LoadConfig()
    {
        // 注意：必须重新从磁盘加载（AppHost.Config 是启动时快照，保存后不会自动刷新）
        var config = AppConfigStore.Load();
        NapCatScript = config.NapCat.ScriptPath;
        WebUiPort = config.NapCat.WebUiPort;
        OneBotPort = config.OneBot11.Port;
        OneBotToken = config.OneBot11.Token;
        ServiceRunning = AppHost.OneBotServerStarted;
        _manager = new NapCatManager(config.NapCat);
        _manager.ManagerLog += (_, line) => Log(line);
        RefreshNapCatStatus();
        var token = _manager.WebUiToken;
        if (token is not null)
        {
            WebUiToken = token;
            _webUi = new NapCatWebUiClient($"http://127.0.0.1:{WebUiPort}", token);
        }
    }

    private void SaveConfig()
    {
        var config = AppConfigStore.Load();
        config.NapCat.ScriptPath = NapCatScript;
        config.NapCat.Directory = Path.GetDirectoryName(NapCatScript) ?? ""; // 派生目录（webui.json 定位）
        config.NapCat.WebUiPort = WebUiPort;
        config.NapCat.WebUiToken = WebUiToken;
        config.OneBot11.Port = OneBotPort;
        config.OneBot11.Token = OneBotToken;
        config.OneBot11.Enabled = true;
        AppConfigStore.Save(config);
        AppHost.Router.Config.OneBot11 = config.OneBot11;
        Log("配置已保存");
        LoadConfig();
    }

    private async Task StartNapCatAsync()
    {
        try
        {
            if (_manager is null) return;

            // ① 主动补入反向 WS 配置（模板 + 已登录账号），NapCat 起连后即可直连本服务
            try
            {
                _manager.InjectReverseWsConfig(OneBotPort, OneBotToken);
            }
            catch (Exception ex)
            {
                Log($"补入连接配置失败（可稍后在卡片③手动补入）：{ex.Message}");
            }

            // ② 拉起 NapCat
            await _manager.StartAsync();
            RefreshNapCatStatus();

            // ③ 服务绑定：OneBot 服务未启动则自动拉起
            if (!AppHost.OneBotServerStarted)
            {
                try
                {
                    await AppHost.StartOneBot11Async();
                    ServiceRunning = AppHost.OneBotServerStarted;
                    Log("已自动启动 OneBot 服务（服务绑定）");
                }
                catch (Exception ex)
                {
                    Log($"自动启动 OneBot 服务失败：{ex.Message}");
                }
            }

            await Task.Delay(4000); // 等 WebUI 起来
            ReloadWebUi();          // NapCat 启动会刷新 webui.json，重读 Token 并重建客户端
            StartStatusPolling();   // 快速登录可能已自动完成，立即开始状态轮询
            await RefreshQrAsync();
        }
        catch (Exception ex) { Log($"启动失败：{ex.Message}"); }
    }

    /// <summary>手动补入反向 WS 配置（卡片③按钮）。</summary>
    private void InjectConfig()
    {
        try
        {
            if (_manager is null) { Log("配置未初始化"); return; }
            _manager.InjectReverseWsConfig(OneBotPort, OneBotToken);
        }
        catch (Exception ex) { Log($"补入失败：{ex.Message}"); }
    }

    private async Task StopNapCatAsync()
    {
        if (_manager is null) return;
        await _manager.StopAsync();
        RefreshNapCatStatus();
    }

    /// <summary>NapCat 启动后重读 webui.json 的 Token 并重建 WebUI 客户端。</summary>
    private void ReloadWebUi()
    {
        if (_manager is null) return;
        var token = _manager.WebUiToken;
        if (string.IsNullOrEmpty(token))
        {
            Log("WebUI Token 未读取到（确认 NapCat 已完全启动，或手动填写 Token）");
            return;
        }
        if (token != WebUiToken)
        {
            WebUiToken = token;
            Log("WebUI Token 已自动读取");
        }
        _webUi = new NapCatWebUiClient($"http://127.0.0.1:{WebUiPort}", token);
    }

    private void RefreshNapCatStatus()
    {
        NapCatStatus = _manager is null ? "未配置"
            : string.IsNullOrWhiteSpace(_manager.Config.ScriptPath) ? "未配置启动脚本"
            : !_manager.IsInstalled ? "脚本不存在（重新选择）"
            : _manager.IsRunning ? "运行中" : "已停止";
    }

    private async Task RefreshQrAsync()
    {
        try
        {
            if (_webUi is null) { Log("WebUI Token 未配置（启动 NapCat 后自动读取，或手动填写）"); return; }
            var qr = await _webUi.GetQrCodeAsync();
            QrImage = RenderQr(qr);
            Log("二维码已获取，请用手机 QQ 扫码");
            StartStatusPolling();
        }
        catch (Exception ex) { Log($"获取二维码失败：{ex.Message}"); }
    }

    private void StartStatusPolling()
    {
        _timer ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3),
        };
        _timer.Tick -= OnPollTick;
        _timer.Tick += OnPollTick;
        _timer.Start();
    }

    private async void OnPollTick(object? sender, EventArgs e)
    {
        if (_webUi is null) return;
        try
        {
            var status = await _webUi.CheckLoginStatusAsync();
            var json = status.GetRawText();
            StatusText = SummarizeStatus(json);
            if (json.Contains("\"isOnline\":true", StringComparison.OrdinalIgnoreCase) ||
                json.Contains("\"online\":true", StringComparison.OrdinalIgnoreCase))
            {
                StatusText = "已在线 ✅";
                _timer?.Stop();
                Log("QQ 已登录（在线）");
            }
        }
        catch (Exception ex)
        {
            StatusText = $"状态查询失败：{ex.Message}";
        }
    }

    private static string SummarizeStatus(string json) => json.Length > 160 ? json[..160] + "…" : json;

    private async Task QuickLoginAsync()
    {
        try
        {
            if (_webUi is null) { Log("WebUI 未连接"); return; }
            var result = await _webUi.SetQuickLoginAsync(QuickLoginUin);
            Log($"快速登录请求已发送：{SummarizeStatus(result.GetRawText())}");
            StartStatusPolling();
        }
        catch (Exception ex) { Log($"快速登录失败：{ex.Message}"); }
    }

    private async Task StartServiceAsync()
    {
        try
        {
            await AppHost.StartOneBot11Async();
            ServiceRunning = AppHost.OneBotServerStarted;
        }
        catch (Exception ex) { Log($"服务启动失败：{ex.Message}"); }
    }

    private async Task StopServiceAsync()
    {
        await AppHost.StopOneBot11Async();
        ServiceRunning = AppHost.OneBotServerStarted;
    }

    /// <summary>从 WebUI 响应渲染二维码图片：data URL 直接解码，普通 URL 本地生成二维码。</summary>
    private static BitmapImage? RenderQr(System.Text.Json.JsonElement qr)
    {
        string? dataUrl = FindString(qr, "qrcode") ?? FindString(qr, "qr") ?? FindString(qr, "base64");
        if (dataUrl is not null && dataUrl.StartsWith("data:image"))
        {
            var base64 = dataUrl[(dataUrl.IndexOf(',') + 1)..];
            return BitmapFromBytes(Convert.FromBase64String(base64));
        }
        var url = FindString(qr, "url") ?? dataUrl;
        if (!string.IsNullOrEmpty(url))
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
            using var code = new PngByteQRCode(data);
            return BitmapFromBytes(code.GetGraphic(8));
        }
        return null;
    }

    private static BitmapImage? BitmapFromBytes(byte[] png)
    {
        var image = new BitmapImage();
        using var ms = new MemoryStream(png);
        ms.Position = 0;
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = ms;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static string? FindString(System.Text.Json.JsonElement el, string name)
    {
        if (el.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
        foreach (var p in el.EnumerateObject())
        {
            if (p.Value.ValueKind == System.Text.Json.JsonValueKind.String &&
                string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                return p.Value.GetString();
            if (p.Value.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                var found = FindString(p.Value, name);
                if (found is not null) return found;
            }
        }
        return null;
    }

    /// <summary>日志绑定：NapCat 相关日志统一发往全局日志流（「运行日志」页可查）。</summary>
    private void Log(string line) => AppHost.Log($"[NapCat] {line}");
}
