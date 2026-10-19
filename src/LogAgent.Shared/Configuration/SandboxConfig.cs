using LogAgent.Shared.Platform;

namespace LogAgent.Shared.Configuration;

/// <summary>
/// 沙箱执行环境配置（规划 3.9）。
/// 默认白名单/黑名单按当前平台生成：Windows 用 cmd 系命令，Linux/macOS 用 POSIX 命令。
/// 已保存到 config.json 的配置优先（读取时字段存在即用用户值，不会因升级被覆盖）。
/// </summary>
public sealed class SandboxConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>Shell 命令超时（秒），超时杀进程树。</summary>
    public int CommandTimeoutSeconds { get; set; } = 30;

    /// <summary>命令输出截断上限（KB），超出部分丢弃防上下文污染。</summary>
    public int MaxOutputKb { get; set; } = 32;

    /// <summary>下载文件大小上限（MB）。</summary>
    public int MaxDownloadMb { get; set; } = 50;

    /// <summary>允许的可执行名（第一个 token 匹配，不区分大小写）。默认按平台生成。</summary>
    public List<string> WhitelistCommands { get; set; } = [.. PlatformShell.Current.DefaultWhitelist];

    /// <summary>黑名单正则（命中即拒绝，优先于白名单）。默认按平台生成。</summary>
    public List<string> BlacklistPatterns { get; set; } = [.. PlatformShell.Current.DefaultBlacklist];

    /// <summary>把白名单/黑名单恢复为当前平台默认值（保留其余字段）。</summary>
    public void ResetCommandRulesToPlatformDefaults()
    {
        WhitelistCommands = [.. PlatformShell.Current.DefaultWhitelist];
        BlacklistPatterns = [.. PlatformShell.Current.DefaultBlacklist];
    }

    /// <summary>
    /// 白名单里是否残留 Windows 专有命令（findstr/type/dir/where/more）——用于 Unix 首次运行时的迁移提示。
    /// 判定目的是「要不要提醒用户」，不会自动改动配置。
    /// </summary>
    public IReadOnlyList<string> WindowsOnlyCommandsInWhitelist()
    {
        if (PlatformInfo.Current == OsFamily.Windows) return [];
        return [.. WhitelistCommands.Where(PosixShell.IsWindowsOnlyCommand)];
    }
}
