using System.Text.RegularExpressions;

namespace LogAgent.Training;

/// <summary>导出前强制脱敏（规划 3.8）：手机号/IP/密钥/带凭据URL/邮箱 → 占位符。</summary>
public static partial class Redactor
{
    [GeneratedRegex(@"1[3-9]\d{9}")]
    private static partial Regex Phone();

    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b")]
    private static partial Regex Ip();

    [GeneratedRegex(@"(?:sk|pk|rk)-[A-Za-z0-9_-]{16,}", RegexOptions.IgnoreCase)]
    private static partial Regex ApiKey();

    [GeneratedRegex(@"(?i)(?:api[_-]?key|token|secret|password)\s*[=:]\s*\S+")]
    private static partial Regex SecretAssignment();

    [GeneratedRegex(@"https?://[^\s/:@]+:[^\s/@]+@")]
    private static partial Regex UrlCredential();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    public static string Apply(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var result = text;
        result = UrlCredential().Replace(result, "<URL-CRED>://");
        result = SecretAssignment().Replace(result, m => $"{m.Value[..m.Value.IndexOfAny(['=', ':'])]}=<SECRET>");
        result = ApiKey().Replace(result, "<API-KEY>");
        result = Phone().Replace(result, "<PHONE>");
        result = Ip().Replace(result, "<IP>");
        result = Email().Replace(result, "<EMAIL>");
        return result;
    }
}
