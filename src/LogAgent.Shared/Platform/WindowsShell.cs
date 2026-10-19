namespace LogAgent.Shared.Platform;

/// <summary>Windows cmd 实现：`cmd.exe /c chcp 65001&gt;nul &amp; 命令`（先切 UTF-8 代码页，中文输出不乱码）。</summary>
public sealed class WindowsShell : IShell
{
    public string Id => "cmd";

    public string DisplayName => "Windows cmd";

    public bool CaseSensitivePaths => false;

    // 与 v0.7.0 的 SandboxConfig 默认值保持一致（Windows 用户已有配置不受影响）
    public IReadOnlyList<string> DefaultWhitelist { get; } =
    [
        "grep", "findstr", "find", "sort", "uniq", "wc", "head", "tail",
        "awk", "sed", "cut", "type", "dir", "more", "echo", "where", "python", "python3",
    ];

    public IReadOnlyList<string> DefaultBlacklist { get; } =
    [
        @"rm\s+(-[a-z]*r[a-z]*f|-[a-z]*f[a-z]*r)", @"del\s+/[sq]", @"rd\s+/s",
        @"format(\.com)?\s+[a-z]:", @"reg\s+(add|delete|import)",
        @"net\s+(user|localgroup)", @"schtasks", @"taskkill", @"shutdown",
        @"powershell", @"pwsh", @"iex\b", @"invoke-expression", @"certutil", @"bitsadmin",
        @"mklink", @"vssadmin", @"bcdedit", @"diskpart",
        @"\b(curl|wget)\b.*\|\s*(bash|sh|zsh)",
        @"\b(echo|type)\b.*\s*>\s*\\\\(?!.*sandbox)",
    ];

    public string RunShellHint =>
        "可执行文件受沙箱白名单限制：findstr、find、type、more、sort、dir、python 等；" +
        "示例：findstr /n /i \"Exception\" 文件名.log；more +100 文件名.log（从第 100 行看起）。";

    public string RunShellOneLiner =>
        "run_shell：执行命令进一步处理（工作目录=files 目录，Windows cmd，如 findstr /n /i \"error\" 文件名）";

    public ShellSpec BuildLaunch(string command) => new(
        FileName: "cmd.exe",
        // 与 v0.7.0 完全一致：先切 UTF-8 代码页再执行命令（读取侧固定 UTF-8 解码）
        Arguments: $"/c chcp 65001>nul & {command}",
        ArgumentList: null,
        Environment: new Dictionary<string, string> { ["PYTHONIOENCODING"] = "utf-8" });
}
