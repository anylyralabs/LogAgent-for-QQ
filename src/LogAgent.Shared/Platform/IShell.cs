namespace LogAgent.Shared.Platform;

/// <summary>
/// 一次 shell 启动的完整描述（文件名 / 参数 / 额外环境变量）。
/// 优先给 <see cref="ArgumentList"/>：Unix 下把整条命令作为单个参数传给 <c>-c</c>，
/// 避免 <see cref="ProcessStartInfo.Arguments"/> 字符串拼接时的引号与空格歧义。
/// </summary>
public sealed record ShellSpec(
    string FileName,
    string? Arguments,
    IReadOnlyList<string>? ArgumentList,
    IReadOnlyDictionary<string, string> Environment);

/// <summary>
/// 平台 shell 抽象：沙箱执行、提示词文案、路径大小写规则都从这里取。
/// 新增平台只需实现此接口并在 <see cref="PlatformShell"/> 里挂上。
/// </summary>
public interface IShell
{
    /// <summary>短标识（cmd / sh），用于日志与配置说明。</summary>
    string Id { get; }

    /// <summary>提示词里展示的 shell 名称（如「Windows cmd」/「POSIX shell」）。</summary>
    string DisplayName { get; }

    /// <summary>文件系统路径是否区分大小写。</summary>
    bool CaseSensitivePaths { get; }

    /// <summary>沙箱默认白名单（新建配置时使用）。</summary>
    IReadOnlyList<string> DefaultWhitelist { get; }

    /// <summary>沙箱默认黑名单（与白名单同时生效，命中即拒绝）。</summary>
    IReadOnlyList<string> DefaultBlacklist { get; }

    /// <summary>run_shell 工具描述里的「可用命令 + 写法示例」一句。</summary>
    string RunShellHint { get; }

    /// <summary>MessageEnricher 里的一行指引（含示例命令）。</summary>
    string RunShellOneLiner { get; }

    /// <summary>把一条用户/模型给的命令行包装成可执行的进程启动参数。</summary>
    ShellSpec BuildLaunch(string command);
}
