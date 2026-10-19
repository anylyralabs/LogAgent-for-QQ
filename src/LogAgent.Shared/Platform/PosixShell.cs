namespace LogAgent.Shared.Platform;

/// <summary>
/// POSIX shell 实现（Linux / macOS / Android）：默认 <c>/bin/sh -c 命令</c>。
/// 可用环境变量 <c>LOGAGENT_SHELL</c> 指定别的 shell（如 <c>/bin/bash</c>），命令本身不拼接字符串以防注入。
/// </summary>
public sealed class PosixShell : IShell
{
    private static readonly string[] WindowsOnlyCommands = ["findstr", "type", "dir", "where", "more"];

    private readonly string _shellPath;

    public PosixShell(string? shellPath = null)
    {
        _shellPath = string.IsNullOrWhiteSpace(shellPath) ? DefaultShellPath() : shellPath;
    }

    public string Id => "sh";

    public string DisplayName => "POSIX shell";

    public bool CaseSensitivePaths => true;

    public IReadOnlyList<string> DefaultWhitelist { get; } =
    [
        "grep", "sed", "awk", "sort", "uniq", "wc", "head", "tail", "cut", "find", "xargs",
        "cat", "ls", "stat", "file", "echo", "python3", "python",
    ];

    public IReadOnlyList<string> DefaultBlacklist { get; } =
    [
        // 破坏性文件操作
        @"\brm\s+(-[a-zA-Z]*r[a-zA-Z]*f|-[a-zA-Z]*f[a-zA-Z]*r)",
        @"\b(shred|unlink)\b",
        @"\bmkfs(\.\w+)?\b", @"\bdd\s+.*of=\s*/dev/", @"\bdd\s+.*of=/dev/",
        // 提权与系统控制（锚定「命令位置」，避免 grep -i "shutdown" 这类正常日志检索被误杀）
        @"(?:^|[;&|]\s*)\s*(?:sudo|doas)\b",
        @"(?:^|[;&|]\s*)\s*(?:shutdown|reboot|halt|poweroff)\b",
        @"(?:^|[;&|]\s*)\s*systemctl\s+(?:stop|restart|disable|mask|kill)\b",
        @"(?:^|[;&|]\s*)\s*service\s+\S+\s+(?:stop|restart)\b",
        @"(?:^|[;&|]\s*)\s*(?:crontab|chkconfig)\b",
        // 权限与所有权语义变更（/tmp 内允许，便于正常日志处理）
        @"\bchmod\s+(-R\s+)?[0-7]{3,4}\s+/(?!tmp)",
        @"\bchown\s+(-R\s+)?\S+\s+/(?!tmp)",
        // 网络管道执行 / 动态求值
        @"\b(?:curl|wget)\b.*\|\s*(?:ba|z|k)?sh",
        @"\beval\b", @"\bexec\s+\d",
        @"\bexport\s+PATH=", @"\bLD_PRELOAD=",
        // 凭据与敏感文件
        @"\b(cat|head|tail|less|more|strings)\b[^\n]*\b(?:/etc/shadow|id_rsa|\.ssh/|\.aws/|\.kube/)",
    ];

    public string RunShellHint =>
        "可执行文件受沙箱白名单限制：grep、sed、awk、sort、head、tail、cut、find、wc、python3 等；" +
        "示例：grep -n -i \"Exception\" 文件名.log（带行号搜索）；" +
        "sed -n '100,200p' 文件名.log（看第 100-200 行）；tail -n 300 文件名.log（看结尾 300 行）。";

    public string RunShellOneLiner =>
        "run_shell：执行命令进一步处理（工作目录=files 目录，POSIX shell，如 grep -n -i \"error\" 文件名）";

    public ShellSpec BuildLaunch(string command) => new(
        FileName: _shellPath,
        Arguments: null,
        // 整条命令作为单个 argv 传给 -c：不做字符串插值，路径里的空格与引号原样保留
        ArgumentList: ["-c", command],
        Environment: new Dictionary<string, string> { ["LANG"] = "C.UTF-8" });

    /// <summary>是否为 Windows 专有命令名（用于旧配置迁移提示）。</summary>
    public static bool IsWindowsOnlyCommand(string name) =>
        WindowsOnlyCommands.Contains(name.Trim().ToLowerInvariant());

    private static string DefaultShellPath()
    {
        var env = Environment.GetEnvironmentVariable("LOGAGENT_SHELL");
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
        return File.Exists("/bin/sh") ? "/bin/sh" : "/bin/bash";
    }
}
