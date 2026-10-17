using System.Collections.ObjectModel;
using System.Windows;
using LogAgent.App.Infrastructure;
using LogAgent.Routing;
using LogAgent.Shared;
using LogAgent.Shared.Chat;
using LogAgent.Shared.Configuration;

namespace LogAgent.App.Pages;

public sealed class ProviderItem(ProviderConfig config) : ObservableObject
{
    public string Id { get; } = config.Id;
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Endpoint { get => _endpoint; set => Set(ref _endpoint, value); }
    public string ApiKey { get => _apiKey; set => Set(ref _apiKey, value); }
    public string ChatModel { get => _chatModel; set => Set(ref _chatModel, value); }
    public string? EmbeddingModel { get => _embeddingModel; set => Set(ref _embeddingModel, value); }
    public int MaxTokens { get => _maxTokens; set => Set(ref _maxTokens, value); }
    public int ContextTokens { get => _contextTokens; set => Set(ref _contextTokens, value); }
    public bool ReasoningModel { get => _reasoningModel; set => Set(ref _reasoningModel, value); }
    public double Temperature { get => _temperature; set => Set(ref _temperature, value); }
    public int TimeoutSeconds { get => _timeoutSeconds; set => Set(ref _timeoutSeconds, value); }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

    /// <summary>上下文窗口显示（如 960K；无元数据时 —）。</summary>
    public string ContextDisplay => ContextTokens > 0 ? $"{ContextTokens / 1000.0:0.#}K" : "—";

    private string _name = config.Name;
    private string _endpoint = config.Endpoint;
    private string _apiKey = config.ApiKey;
    private string _chatModel = config.ChatModel;
    private string? _embeddingModel = config.EmbeddingModel;
    private int _maxTokens = config.MaxTokens;
    private int _contextTokens = config.ContextTokens;
    private bool _reasoningModel = config.ReasoningModel;
    private double _temperature = config.Temperature;
    private int _timeoutSeconds = config.TimeoutSeconds;
    private bool _enabled = config.Enabled;

    public ProviderConfig ToConfig() => new()
    {
        Id = Id, Name = Name, Endpoint = Endpoint, ApiKey = ApiKey,
        ChatModel = ChatModel, EmbeddingModel = EmbeddingModel,
        MaxTokens = MaxTokens, ContextTokens = ContextTokens, ReasoningModel = ReasoningModel,
        Temperature = Temperature, TimeoutSeconds = TimeoutSeconds,
        Enabled = Enabled,
    };
}

public sealed class RuleItem(RouterRule rule, string chainText) : ObservableObject
{
    public TaskType TaskType
    {
        get => _taskType;
        set
        {
            if (Set(ref _taskType, value))
                Raise(nameof(DisplayName));
        }
    }

    /// <summary>中文任务名（设置项列表左侧标签）。</summary>
    public string DisplayName => TaskTypeDisplay.Name(TaskType);

    public string ChainText { get => _chainText; set => Set(ref _chainText, value); }

    private TaskType _taskType = rule.TaskType;
    private string _chainText = chainText;
}

/// <summary>TaskType → 中文显示名。</summary>
public static class TaskTypeDisplay
{
    public static string Name(TaskType type) => type switch
    {
        TaskType.Chat => "聊天对话",
        TaskType.LogAnalysis => "日志分析",
        TaskType.DeepReasoning => "深度推理",
        TaskType.Summary => "摘要提炼",
        TaskType.Embedding => "向量化",
        _ => type.ToString(),
    };
}

public sealed class ProvidersViewModel : ObservableObject
{
    public ObservableCollection<ProviderItem> Providers { get; } = [];
    public ObservableCollection<RuleItem> Rules { get; } = [];
    public IReadOnlyList<TaskType> AllTaskTypes { get; } = Enum.GetValues<TaskType>();

    public ProviderItem? SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            if (Set(ref _selectedProvider, value))
                Raise(nameof(CurrentSelectionText));
        }
    }

    /// <summary>右侧测试卡：当前选中 Provider 描述。</summary>
    public string CurrentSelectionText => SelectedProvider is null
        ? "未选中 Provider（在左侧列表选择一行）"
        : $"已选中：{SelectedProvider.Name}";

    /// <summary>连接测试结果（就地显示，不弹窗）。</summary>
    public string TestResult
    {
        get => _testResult;
        private set => Set(ref _testResult, value);
    }

    public System.Windows.Media.Brush TestResultBrush
    {
        get => _testResultBrush;
        private set => Set(ref _testResultBrush, value);
    }

    public RelayCommand RemoveProviderCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand TestSelectedCommand { get; }

    private ProviderItem? _selectedProvider;
    private string _testResult = "选中一个 Provider 后可测试连通性";
    private System.Windows.Media.Brush _testResultBrush =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x72, 0x72));

    public ProvidersViewModel()
    {
        RemoveProviderCommand = new RelayCommand(RemoveProvider, () => SelectedProvider is not null);
        SaveCommand = new RelayCommand(Save);
        TestSelectedCommand = new RelayCommand(() => _ = TestSelectedAsync(), () => SelectedProvider is not null);
        Load();
    }

    private void Load()
    {
        var config = AppConfigStore.Load();
        Providers.Clear();
        foreach (var p in config.Providers)
            Providers.Add(new ProviderItem(p));
        Rules.Clear();
        foreach (var r in config.Rules)
        {
            var names = r.ProviderChain
                .Select(id => config.Providers.FirstOrDefault(p => p.Id == id)?.Name ?? id);
            Rules.Add(new RuleItem(r, string.Join(", ", names)));
        }
    }

    private void RemoveProvider()
    {
        if (SelectedProvider is { } item) Providers.Remove(item);
    }

    private void Save()
    {
        var config = AppConfigStore.Load();
        var providers = Providers.Select(p => p.ToConfig()).ToList();
        var nameToId = providers.ToDictionary(p => p.Name, p => p.Id, StringComparer.OrdinalIgnoreCase);
        // 空链兜底：指向第一个启用的 Provider（配好模型即可直接可用，无需逐条填链）
        var firstEnabledId = providers.FirstOrDefault(p => p.Enabled)?.Id;

        foreach (var rule in Rules)
        {
            var chain = rule.ChainText
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(n => nameToId.TryGetValue(n, out var id) ? id : n)
                .Distinct()
                .ToList();
            if (chain.Count == 0 && firstEnabledId is not null)
                chain = [firstEnabledId];
            var target = config.Rules.FirstOrDefault(r => r.TaskType == rule.TaskType);
            if (target is null) config.Rules.Add(new RouterRule { TaskType = rule.TaskType, ProviderChain = chain });
            else target.ProviderChain = chain;
        }
        // 保持未在 UI 中出现的其它配置字段
        config.Providers = providers;
        AppConfigStore.Save(config);
        MessageBox.Show(
            firstEnabledId is not null
                ? $"已保存到 {AppConfigStore.GetConfigPath()}\n（空的优先链已自动指向第一个启用的 Provider）"
                : $"已保存到 {AppConfigStore.GetConfigPath()}",
            "保存", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async Task TestSelectedAsync()
    {
        if (SelectedProvider is not { } item) return;
        var config = item.ToConfig();
        if (string.IsNullOrWhiteSpace(config.ChatModel))
        {
            SetTestResult("请先填写 ChatModel（本地服务 API Key 可留空）", ok: false);
            return;
        }
        SetTestResult("⏳ 测试中…", ok: null);
        try
        {
            var client = new OpenAiCompatClient(config);
            var result = await client.CompleteAsync(new ChatRequest(
                [ChatTurn.User(Prompts.ConnectionProbe)], MaxTokens: 16, Temperature: 0));
            SetTestResult(
                $"✅ 连通成功\n模型：{result.Model}\n延迟：{result.Latency.TotalMilliseconds:F0} ms\n回复：{result.Content}",
                ok: true);
        }
        catch (Exception ex)
        {
            SetTestResult($"❌ 连接失败：{ex.Message}", ok: false);
        }
    }

    private void SetTestResult(string text, bool? ok)
    {
        TestResult = text;
        TestResultBrush = new System.Windows.Media.SolidColorBrush(ok switch
        {
            true => System.Windows.Media.Color.FromRgb(0x0F, 0x7B, 0x0F),
            false => System.Windows.Media.Color.FromRgb(0xC4, 0x2B, 0x1C),
            _ => System.Windows.Media.Color.FromRgb(0x72, 0x72, 0x72),
        });
    }
}
