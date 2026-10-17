using System.Globalization;
using System.Windows;
using System.Windows.Media;
using LogAgent.Routing;
using LogAgent.Shared;
using LogAgent.Shared.Chat;
using LogAgent.Shared.Configuration;

namespace LogAgent.App.Pages;

/// <summary>
/// 模型（Provider）编辑弹窗：添加/编辑一个 OpenAI 兼容接口通道；支持就地「测试连接」，
/// 保存后通过 <see cref="Result"/> 返回（ShowDialog() == true 时有效）。
/// </summary>
public partial class ProviderEditWindow : Window
{
    private readonly string _id;
    private readonly Dictionary<string, OpenAiCompatClient.ModelInfo> _modelInfos = new(StringComparer.OrdinalIgnoreCase);
    private int _contextTokens;
    private bool _reasoningModel;

    /// <summary>保存后的配置结果。</summary>
    public ProviderConfig? Result { get; private set; }

    public ProviderEditWindow(ProviderConfig? existing = null)
    {
        InitializeComponent();
        if (existing is null)
        {
            _id = Guid.NewGuid().ToString("N")[..8];
            Title = "LogAgent — 添加模型";
            HeaderText.Text = "添加模型";
            MaxTokensBox.Text = "4096";
            TemperatureBox.Text = "0.7";
            TimeoutBox.Text = "120";
        }
        else
        {
            _id = existing.Id;
            Title = "LogAgent — 编辑模型";
            HeaderText.Text = "编辑模型";
            NameBox.Text = existing.Name;
            EndpointBox.Text = existing.Endpoint;
            ApiKeyBox.Text = existing.ApiKey;
            ChatModelCombo.Text = existing.ChatModel;
            EmbeddingModelCombo.Text = existing.EmbeddingModel ?? "";
            MaxTokensBox.Text = existing.MaxTokens.ToString(CultureInfo.InvariantCulture);
            TemperatureBox.Text = existing.Temperature.ToString(CultureInfo.InvariantCulture);
            TimeoutBox.Text = existing.TimeoutSeconds.ToString(CultureInfo.InvariantCulture);
            EnabledToggle.IsChecked = existing.Enabled;
            _contextTokens = existing.ContextTokens;
            _reasoningModel = existing.ReasoningModel;
            ShowMetaHint(existing.ContextTokens, 0, existing.ReasoningModel, autoApplied: false);
        }
        ChatModelCombo.SelectionChanged += (_, _) => ApplyModelMeta();
        Loaded += (_, _) => NameBox.Focus();
    }

    /// <summary>模型下拉选择变化：命中元数据时自动填充输出预算并展示能力提示。</summary>
    private void ApplyModelMeta()
    {
        if (!_modelInfos.TryGetValue(ChatModelCombo.Text.Trim(), out var info)) return;
        _contextTokens = info.ContextTokens;
        _reasoningModel = info.Reasoning;
        if (info.MaxOutputTokens > 0)
            MaxTokensBox.Text = info.MaxOutputTokens.ToString(CultureInfo.InvariantCulture);
        ShowMetaHint(info.ContextTokens, info.MaxOutputTokens, info.Reasoning, autoApplied: info.MaxOutputTokens > 0);
    }

    private void ShowMetaHint(int contextTokens, int maxOutput, bool reasoning, bool autoApplied)
    {
        var parts = new List<string>();
        if (contextTokens > 0) parts.Add($"上下文 {FormatK(contextTokens)}");
        if (maxOutput > 0) parts.Add($"最大输出 {FormatK(maxOutput)}");
        if (reasoning) parts.Add("思考模型");
        if (parts.Count == 0)
        {
            ModelMetaHint.Visibility = Visibility.Collapsed;
            return;
        }
        ModelMetaHint.Text = "模型元数据：" + string.Join(" · ", parts)
            + (autoApplied ? "（输出预算已自动填充；思考模型的预算需给足，否则回复会被思考消耗掉）" : "");
        ModelMetaHint.Visibility = Visibility.Visible;
    }

    private static string FormatK(int v) => v >= 1000 ? $"{v / 1000.0:0.#}K" : v.ToString(CultureInfo.InvariantCulture);

    /// <summary>从表单读取配置；校验失败时就地提示并返回 null（测试连接等场景可豁免名称必填）。</summary>
    private ProviderConfig? ReadForm(bool requireName = true)
    {
        var name = NameBox.Text.Trim();
        if (requireName && name.Length == 0)
        {
            SetStatus("请填写名称（分流规则里会用到这个名字）", StatusKind.Error);
            NameBox.Focus();
            return null;
        }
        var endpoint = EndpointBox.Text.Trim();
        if (endpoint.Length == 0)
        {
            SetStatus("请填写 API 地址", StatusKind.Error);
            EndpointBox.Focus();
            return null;
        }
        if (!endpoint.Contains("://", StringComparison.Ordinal))
            endpoint = "https://" + endpoint; // 容错：只填域名时自动补协议头

        return new ProviderConfig
        {
            Id = _id,
            Name = name,
            Endpoint = endpoint,
            ApiKey = ApiKeyBox.Text.Trim(),
            ChatModel = ChatModelCombo.Text.Trim(),
            EmbeddingModel = string.IsNullOrWhiteSpace(EmbeddingModelCombo.Text) ? null : EmbeddingModelCombo.Text.Trim(),
            MaxTokens = ParseInt(MaxTokensBox.Text, 4096, 1, 131072),
            ContextTokens = _contextTokens,
            ReasoningModel = _reasoningModel,
            Temperature = ParseDouble(TemperatureBox.Text, 0.7, 0, 2),
            TimeoutSeconds = ParseInt(TimeoutBox.Text, 120, 5, 1200),
            Enabled = EnabledToggle.IsChecked == true,
        };
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var config = ReadForm();
        if (config is null) return;
        Result = config;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>主动拉取模型列表：GET {Endpoint}/models，填入两个下拉框（Chat / 向量自动分类），并缓存元数据。</summary>
    private async void FetchModels_Click(object sender, RoutedEventArgs e)
    {
        var endpoint = EndpointBox.Text.Trim();
        if (endpoint.Length == 0)
        {
            SetStatus("请先填写 API 地址，再获取模型列表", StatusKind.Error);
            EndpointBox.Focus();
            return;
        }
        if (!endpoint.Contains("://", StringComparison.Ordinal))
            endpoint = "https://" + endpoint;

        var probe = new ProviderConfig
        {
            Endpoint = endpoint,
            ApiKey = ApiKeyBox.Text.Trim(),
            TimeoutSeconds = 30,
        };
        FetchModelsButton.IsEnabled = false;
        SetStatus("⏳ 正在获取模型列表…", StatusKind.Info);
        try
        {
            var client = new OpenAiCompatClient(probe);
            var models = await client.ListModelInfosAsync();
            if (models.Count == 0)
            {
                SetStatus("接口未返回任何模型（响应中没有 data[].id）", StatusKind.Error);
                return;
            }

            _modelInfos.Clear();
            foreach (var m in models) _modelInfos[m.Id] = m;

            // 轻量分类：名字含 embed 的进「向量模型」候选，其余进「Chat 模型」候选；某类为空时两边都给全量
            var ids = models.Select(m => m.Id).ToList();
            var embeddings = ids.Where(m => m.Contains("embed", StringComparison.OrdinalIgnoreCase)).ToList();
            var chatModels = ids.Where(m => !m.Contains("embed", StringComparison.OrdinalIgnoreCase)).ToList();
            ChatModelCombo.ItemsSource = chatModels.Count > 0 ? chatModels : ids;
            EmbeddingModelCombo.ItemsSource = embeddings.Count > 0 ? embeddings : ids;

            var withMeta = models.Count(m => m.ContextTokens > 0 || m.MaxOutputTokens > 0 || m.Reasoning);
            SetStatus($"✅ 获取到 {models.Count} 个模型（{withMeta} 个带上下文元数据），可在下拉框中选择（Chat {ChatModelCombo.Items.Count} / 向量 {EmbeddingModelCombo.Items.Count}）",
                StatusKind.Ok);
            if (string.IsNullOrWhiteSpace(ChatModelCombo.Text) && ChatModelCombo.Items.Count > 0)
                ChatModelCombo.SelectedIndex = 0; // 未填时预选第一个，仍可改
            ApplyModelMeta(); // 预选/当前值命中元数据时自动填充输出预算
        }
        catch (Exception ex)
        {
            SetStatus($"❌ 获取失败：{ex.Message}", StatusKind.Error);
        }
        finally
        {
            FetchModelsButton.IsEnabled = true;
        }
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        var config = ReadForm(requireName: false); // 测试连通性不需要名称
        if (config is null) return;
        if (config.ChatModel.Length == 0)
        {
            SetStatus("请先填写 Chat 模型名再测试", StatusKind.Error);
            ChatModelCombo.Focus();
            return;
        }

        TestBtn.IsEnabled = false;
        SetStatus("⏳ 测试中…", StatusKind.Info);
        try
        {
            var client = new OpenAiCompatClient(config);
            var result = await client.CompleteAsync(new ChatRequest(
                [ChatTurn.User(Prompts.ConnectionProbe)], MaxTokens: 16, Temperature: 0));
            var reply = string.IsNullOrWhiteSpace(result.Content)
                ? "（空回复——思考模型在极短预算下可能只思考不产出；正式对话已按模型预算自动调大）"
                : result.Content;
            SetStatus(
                $"✅ 连通成功 · 模型 {result.Model} · {result.Latency.TotalMilliseconds:F0} ms\n回复：{reply}",
                StatusKind.Ok);
        }
        catch (Exception ex)
        {
            SetStatus($"❌ 连接失败：{ex.Message}", StatusKind.Error);
        }
        finally
        {
            TestBtn.IsEnabled = true;
        }
    }

    private static int ParseInt(string text, int fallback, int min, int max)
        => int.TryParse(text.Trim(), out var v) ? Math.Clamp(v, min, max) : fallback;

    private static double ParseDouble(string text, double fallback, double min, double max)
        => double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? Math.Clamp(v, min, max)
            : fallback;

    private enum StatusKind { Info, Ok, Error }

    private void SetStatus(string text, StatusKind kind)
    {
        StatusText.Text = text;
        StatusText.Visibility = Visibility.Visible;
        StatusText.Foreground = (Brush)FindResource(kind switch
        {
            StatusKind.Ok => "OkFill",
            StatusKind.Error => "ErrorFill",
            _ => "TextSecondary",
        });
    }
}
