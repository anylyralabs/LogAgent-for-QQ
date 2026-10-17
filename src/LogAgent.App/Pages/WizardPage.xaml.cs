using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Controls;
using LogAgent.App.Services;
using LogAgent.Shared.Configuration;

namespace LogAgent.App.Pages;

/// <summary>
/// 首次启动向导（参考主流 AI 桌面客户端的三步图形化初始化模式）：
/// 欢迎 → 配置模型通道（可跳过/可测试）→ NapCat 扫码接入指引。
/// </summary>
public partial class WizardPage : Page
{
    private int _step;

    public WizardPage()
    {
        InitializeComponent();
        ShowStep(0);
    }

    private void ShowStep(int step)
    {
        _step = Math.Clamp(step, 0, 2);
        Step0.Visibility = _step == 0 ? Visibility.Visible : Visibility.Collapsed;
        Step1.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step2.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        StepDots.Text = _step switch
        {
            0 => "● ○ ○",
            1 => "○ ● ○",
            _ => "○ ○ ●",
        };
        PrevBtn.Visibility = _step > 0 ? Visibility.Visible : Visibility.Hidden;
        NextBtn.Content = _step == 2 ? "完成" : "下一步";
        if (_step == 1)
        {
            WizName.Text = string.IsNullOrWhiteSpace(WizName.Text) ? "我的模型" : WizName.Text;
        }
    }

    private void PrevBtn_Click(object sender, RoutedEventArgs e) => ShowStep(_step - 1);

    private void NextBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_step == 1 && TestResult.Text.StartsWith("✅"))
            SaveProvider(); // 测试通过才落配置；未测试则允许下一步时落草稿
        if (_step == 1 && !string.IsNullOrWhiteSpace(WizKey.Password) &&
            !string.IsNullOrWhiteSpace(WizModel.Text) && !TestResult.Text.StartsWith("❌"))
            SaveProvider();

        if (_step < 2)
        {
            ShowStep(_step + 1);
            return;
        }
        Finish();
    }

    private void SkipBtn_Click(object sender, RoutedEventArgs e) => Finish();

    private async void TestBtn_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(WizKey.Password) || string.IsNullOrWhiteSpace(WizModel.Text))
        {
            TestResult.Text = "❌ 请先填 ApiKey 和 ChatModel";
            return;
        }
        TestResult.Text = "⏳ 测试中…";
        TestBtn.IsEnabled = false;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", WizKey.Password);
            using var resp = await http.PostAsJsonAsync(
                $"{WizEndpoint.Text.TrimEnd('/')}/chat/completions",
                new { model = WizModel.Text, messages = new[] { new { role = "user", content = "回复：OK" } }, max_tokens = 8 });
            resp.EnsureSuccessStatusCode();
            TestResult.Text = "✅ 连接成功";
        }
        catch (Exception ex)
        {
            TestResult.Text = $"❌ 连接失败：{ex.Message[..Math.Min(60, ex.Message.Length)]}";
        }
        finally
        {
            TestBtn.IsEnabled = true;
        }
    }

    private void SaveProvider()
    {
        var config = AppConfigStore.Load();
        var provider = config.Providers.FirstOrDefault(p => p.Name == WizName.Text)
            ?? new ProviderConfig();
        provider.Name = WizName.Text.Trim();
        provider.Endpoint = WizEndpoint.Text.Trim();
        provider.ApiKey = WizKey.Password;
        provider.ChatModel = WizModel.Text.Trim();
        if (config.Providers.All(p => p.Id != provider.Id))
            config.Providers.Add(provider);

        // 自动建规则链：所有任务都指向这个 provider
        foreach (var rule in config.Rules)
            if (rule.ProviderChain.Count == 0)
                rule.ProviderChain = [provider.Id];
        config.DefaultProviderId = provider.Id;
        AppConfigStore.Save(config);
        TestResult.Text = "✅ 已保存";
    }

    private void Finish()
    {
        var config = AppConfigStore.Load();
        config.WizardCompleted = true;
        AppConfigStore.Save(config);
        // 通知主窗口进入主界面
        var window = Window.GetWindow(this);
        if (window is MainWindow main)
            main.ExitWizard();
    }
}
