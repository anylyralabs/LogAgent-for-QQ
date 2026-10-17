using System.Text.Json.Nodes;
using LogAgent.Knowledge;
using LogAgent.Shared.Chat;

namespace LogAgent.Skills;

/// <summary>
/// 内置网页浏览工具：web_fetch——按 URL 抓取网页并提取标题与正文。
/// SSRF 守卫默认启用（拒绝内网/环回地址）；富化环节已自动抓取消息里的链接，本工具供模型主动查资料/补救。
/// </summary>
public sealed class WebTools(bool allowPrivate = false)
{
    public const string WebFetchTool = "web_fetch";

    public const string WebFetchParameters =
        """{"type":"object","properties":{"url":{"type":"string","description":"要抓取的网页地址（http/https）"},"max_chars":{"type":"integer","description":"返回正文的最大字符数，默认 8000，上限 16000"}},"required":["url"]}""";

    public event EventHandler<string>? ToolLog;

    public static bool IsBuiltinTool(string name) =>
        string.Equals(name, WebFetchTool, StringComparison.Ordinal);

    public IReadOnlyList<ChatToolDefinition> BuildToolDefinitions() =>
    [
        new(WebFetchTool,
            "抓取网页内容并提取标题与正文文本（自动去除脚本/导航等噪音）。用户想了解某个网页/文档/帖子内容，或需要验证链接、补充资料时使用。仅支持 http/https 公网地址；不接受图片/视频等非文本网页。",
            WebFetchParameters),
    ];

    public async Task<string> ExecuteAsync(string toolName, string argumentsJson, CancellationToken ct = default)
    {
        if (!IsBuiltinTool(toolName)) return $"错误：未知工具 {toolName}";

        string url;
        var maxChars = 8000;
        try
        {
            var node = JsonNode.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson) as JsonObject;
            url = node?["url"]?.GetValue<string>()?.Trim() ?? "";
            if (node?["max_chars"] is { } mc)
            {
                try
                {
                    maxChars = mc.GetValueKind() == System.Text.Json.JsonValueKind.Number
                        ? mc.GetValue<int>()
                        : int.TryParse(mc.GetValue<string>(), out var v) ? v : 8000;
                }
                catch (Exception ex) when (ex is InvalidOperationException or FormatException or OverflowException)
                {
                    maxChars = 8000;
                }
            }
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return "错误：web_fetch 参数不是合法 JSON";
        }

        if (url.Length == 0) return "错误：缺少必填参数 url";
        maxChars = Math.Clamp(maxChars, 500, 16000);

        try
        {
            var page = await WebFetchCore.FetchAsync(url, allowPrivate, maxChars, ct);
            ToolLog?.Invoke(this, $"web_fetch {url} → {page.Text.Length} 字符（{page.Bytes} 字节）");
            var head = $"【网页：{page.Title}】\n来源：{page.Url}\n";
            if (page.Truncated)
                head += $"（正文较长，仅显示前 {maxChars} 字符——可用 read 类方式分段获取的替代：调大 max_chars 或抓取页面内更具体的子页面）\n";
            return head + "--- 正文 ---\n" + page.Text;
        }
        catch (Exception ex)
        {
            ToolLog?.Invoke(this, $"web_fetch {url} 失败：{ex.Message}");
            return $"错误：网页抓取失败（{ex.Message}）";
        }
    }
}
