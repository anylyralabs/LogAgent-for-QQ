using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LogAgent.App.Services;
using LogAgent.Connections;
using LogAgent.Shared.Configuration;
using QRCoder;

namespace LogAgent.App.Pages;

/// <summary>官方 Bot（QQ 开放平台）连接页：应用内扫码绑定（自动拿凭证）→ 凭证配置 → 连接管理。</summary>
public partial class OfficialBotPage : Page
{
    private const string PlatformUrl = "https://q.qq.com";

    private CancellationTokenSource? _qrCts;
    private readonly DispatcherTimer _statusTimer;

    public OfficialBotPage()
    {
        InitializeComponent();
        LoadConfig();
        UpdateStatus();

        // WS 为后台异步连接：定时刷新状态（否则连上了界面也不知道）
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (_, _) => UpdateStatus();
        _statusTimer.Start();

        Unloaded += (_, _) =>
        {
            CancelQr();
            _statusTimer.Stop();
        };
    }

    private void LoadConfig()
    {
        var config = AppConfigStore.Load().OfficialBot;
        AppIdBox.Text = config.AppId;
        SecretBox.Password = config.AppSecret;
        SandboxCheck.IsChecked = config.Sandbox;
        EnabledCheck.IsChecked = config.Enabled;
    }

    private void UpdateStatus()
    {
        var running = AppHost.OfficialBotRunning;
        var pending = AppHost.OfficialBotPending;
        if (running)
        {
            StatusText.Text = "已连接";
            StatusText.Foreground = (System.Windows.Media.Brush)FindResource("OkFill");
        }
        else if (pending)
        {
            StatusText.Text = "连接中…（等待网关握手）";
            StatusText.Foreground = (System.Windows.Media.Brush)FindResource("AccentText");
        }
        else
        {
            StatusText.Text = "未连接";
            StatusText.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondary");
        }
        ConnectBtn.IsEnabled = !running && !pending;
        DisconnectBtn.IsEnabled = running || pending;
    }

    // ---------- 应用内扫码 ----------

    private async void StartQr_Click(object sender, RoutedEventArgs e)
    {
        _qrCts = new CancellationTokenSource();
        var ct = _qrCts.Token;
        StartQrBtn.Visibility = Visibility.Collapsed;
        CancelQrBtn.Visibility = Visibility.Visible;
        QrImage.Visibility = Visibility.Visible;

        var client = new QrConnectClient();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                QrStatus.Text = "正在获取二维码…";
                var url = await client.StartAsync(source: "LogAgent", ct: ct);
                QrImage.Source = RenderQr(url);
                QrStatus.Text = "请用手机 QQ 扫码（二维码过期会自动刷新）";

                QrBindResult result;
                do
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                    result = await client.PollAsync(ct);
                } while (result.Status == QrBindStatus.Pending && !ct.IsCancellationRequested);

                if (ct.IsCancellationRequested) break;
                if (result.Status == QrBindStatus.Completed)
                {
                    AppIdBox.Text = result.AppId ?? "";
                    SecretBox.Password = result.AppSecret ?? "";
                    SandboxCheck.IsChecked = false; // 扫码创建的机器人走正式环境
                    EnabledCheck.IsChecked = true;
                    QrImage.Visibility = Visibility.Collapsed;
                    QrStatus.Text = "✅ 扫码成功，凭证已自动填入下方并保存";
                    Save_Click(sender, e); // 复用保存逻辑
                    break;
                }
                QrStatus.Text = "二维码已过期，正在刷新…";
            }
        }
        catch (OperationCanceledException)
        {
            QrStatus.Text = "已取消扫码";
        }
        catch (Exception ex)
        {
            QrStatus.Text = $"扫码失败：{ex.Message}";
        }
        finally
        {
            StartQrBtn.Visibility = Visibility.Visible;
            CancelQrBtn.Visibility = Visibility.Collapsed;
            if (QrImage.Visibility == Visibility.Visible)
                QrImage.Visibility = Visibility.Collapsed;
        }
    }

    private void CancelQr_Click(object sender, RoutedEventArgs e) => CancelQr();

    private void CancelQr()
    {
        _qrCts?.Cancel();
        _qrCts?.Dispose();
        _qrCts = null;
    }

    /// <summary>用 QRCoder 把 URL 渲染成 BitmapImage。</summary>
    private static BitmapImage RenderQr(string url)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(8);
        var image = new BitmapImage();
        image.BeginInit();
        image.StreamSource = new MemoryStream(png);
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    }

    // ---------- 其他 ----------

    private static void OpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打开浏览器失败：{ex.Message}", "官方 Bot",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenScanPage_Click(object sender, RoutedEventArgs e) => OpenBrowser($"{PlatformUrl}/qqbot/openclaw/login.html");

    private void OpenPlatform_Click(object sender, RoutedEventArgs e) => OpenBrowser(PlatformUrl);

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var config = AppConfigStore.Load();
        config.OfficialBot.AppId = AppIdBox.Text.Trim();
        config.OfficialBot.AppSecret = SecretBox.Password;
        config.OfficialBot.Sandbox = SandboxCheck.IsChecked == true;
        config.OfficialBot.Enabled = EnabledCheck.IsChecked == true;
        AppConfigStore.Save(config);
        SaveHint.Text = "已保存 ✅";
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        ConnectBtn.IsEnabled = false;
        StatusText.Text = "连接中…";
        try
        {
            // 启动后状态由定时器持续刷新（WS 后台握手需要几秒）
            await AppHost.StartOfficialBotAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"连接失败：{ex.Message}", "官方 Bot",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            ConnectBtn.IsEnabled = true;
            return;
        }
        UpdateStatus();
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        await AppHost.StopOfficialBotAsync();
        UpdateStatus();
    }
}
