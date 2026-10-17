namespace LogAgent.Shared.Configuration;

/// <summary>沙箱执行环境配置（规划 3.9）。</summary>
public sealed class SandboxConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>Shell 命令超时（秒），超时杀进程树。</summary>
    public int CommandTimeoutSeconds { get; set; } = 30;

    /// <summary>命令输出截断上限（KB），超出部分丢弃防上下文污染。</summary>
    public int MaxOutputKb { get; set; } = 32;

    /// <summary>下载文件大小上限（MB）。</summary>
    public int MaxDownloadMb { get; set; } = 50;

    /// <summary>允许的可执行名（第一个 token 匹配，不区分大小写）。</summary>
    public List<string> WhitelistCommands { get; set; } =
        ["grep", "findstr", "find", "sort", "uniq", "wc", "head", "tail",
         "awk", "sed", "cut", "type", "dir", "more", "echo", "where", "python", "python3"];

    /// <summary>黑名单正则（命中即拒绝，优先于白名单）。</summary>
    public List<string> BlacklistPatterns { get; set; } =
    [
        @"rm\s+(-[a-z]*r[a-z]*f|-[a-z]*f[a-z]*r)", @"del\s+/[sq]", @"rd\s+/s",
        @"format(\.com)?\s+[a-z]:", @"reg\s+(add|delete|import)",
        @"net\s+(user|localgroup)", @"schtasks", @"taskkill", @"shutdown",
        @"powershell", @"pwsh", @"iex\b", @"invoke-expression", @"certutil", @"bitsadmin",
        @"mklink", @"vssadmin", @"bcdedit", @"diskpart",
        @"\b(curl|wget)\b.*\|\s*(bash|sh|zsh)",
        @"\b(echo|type)\b.*\s*>\s*\\\\(?!.*sandbox)",
    ];
}
