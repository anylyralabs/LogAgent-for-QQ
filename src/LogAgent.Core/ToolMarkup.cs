namespace LogAgent.Core;

/// <summary>
/// 清理模型输出里泄漏的工具调用标记：在"收尾作答"（不带工具定义）的调用中，
/// 个别模型（如 hy4）会把工具调用意图以文本形式吐出（形如 &lt;tool_calls:...&gt;…），
/// 从首个标记处截断，保证发给用户的报告里不出现机器码。
/// </summary>
public static class ToolMarkup
{
    public static string Strip(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var idx = text.IndexOf("<tool_call", StringComparison.OrdinalIgnoreCase);
        return idx < 0 ? text : text[..idx].TrimEnd();
    }
}
