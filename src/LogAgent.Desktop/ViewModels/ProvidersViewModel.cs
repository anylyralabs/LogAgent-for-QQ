using System.Collections.ObjectModel;
using LogAgent.Routing;
using LogAgent.Shared;
using LogAgent.Shared.Chat;
using LogAgent.Shared.Configuration;

namespace LogAgent.Desktop.ViewModels;

/// <summary>模型路由页：Provider 列表（只读展示 + 启用开关 + 连通性测试），规则链概览。</summary>
public sealed class ProvidersViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private string _testResult = "";
    private ProviderItem? _selected;

    public ProvidersViewModel(ShellViewModel shell)
    {
        _shell = shell;
        ReloadCommand = new RelayCommand(Reload);
        TestCommand = new RelayCommand(TestSelectedAsync, () => Selected is not null);
        OpenConfigCommand = new RelayCommand(OpenConfig);
        Reload();
    }

    public ObservableCollection<ProviderItem> Providers { get; } = [];
    public ObservableCollection<RuleItem> Rules { get; } = [];

    public ProviderItem? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value)) TestCommand.RaiseCanExecuteChanged();
        }
    }

    public string TestResult { get => _testResult; private set => Set(ref _testResult, value); }

    public string ConfigPath => AppConfigStore.GetConfigPath();

    public RelayCommand ReloadCommand { get; }
    public RelayCommand TestCommand { get; }
    public RelayCommand OpenConfigCommand { get; }

    public void Reload()
    {
        var config = AppConfigStore.Load();
        if (Providers.Count == 0)
        {
            foreach (var p in config.Providers) Providers.Add(new ProviderItem(p));
            Selected = Providers.FirstOrDefault();
        }

        Rules.Clear();
        foreach (var rule in config.Rules.OrderBy(r => r.TaskType))
            Rules.Add(new RuleItem(rule.TaskType.ToString(), rule.ProviderChain.Count == 0
                ? "(未配置)"
                : string.Join(" → ", rule.ProviderChain)));
    }

    private async Task TestSelectedAsync()
    {
        if (Selected is not { } item) return;
        var config = AppConfigStore.Load().Providers.FirstOrDefault(p => p.Id == item.Id);
        if (config is null)
        {
            TestResult = "该 Provider 已不在配置中（请刷新）";
            return;
        }
        if (string.IsNullOrWhiteSpace(config.ChatModel))
        {
            TestResult = "请先填写 ChatModel";
            return;
        }

        TestResult = "⏳ 测试中…";
        try
        {
            var client = new OpenAiCompatClient(config);
            var result = await client.CompleteAsync(new ChatRequest(
                [ChatTurn.User(Prompts.ConnectionProbe)],
                MaxTokens: 16, Temperature: 0));
            TestResult = $"✅ 连通成功 · {result.Model} · {result.Latency.TotalMilliseconds:F0} ms · 回复：{result.Content}";
        }
        catch (Exception ex)
        {
            TestResult = $"❌ 连接失败：{ex.Message}";
        }
    }

    private void OpenConfig()
    {
        Services.PlatformService.Open(ConfigPath);
        TestResult = $"已尝试用系统默认程序打开：{ConfigPath}";
    }
}

/// <summary>Provider 展示项。</summary>
public sealed class ProviderItem(ProviderConfig config) : ObservableObject
{
    public ProviderConfig Config { get; } = config;
    public string Id => Config.Id;
    public string Name => string.IsNullOrEmpty(Config.Name) ? Config.Id : Config.Name;
    public string Endpoint => Config.Endpoint;
    public string Model => Config.ChatModel;
    public string State => Config.Enabled ? "启用" : "停用";
    public string Badge => Config.ReasoningModel ? "思考模型" : "";
}

/// <summary>分流规则展示项。</summary>
public sealed record RuleItem(string TaskType, string Chain);
