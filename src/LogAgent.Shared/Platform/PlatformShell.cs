namespace LogAgent.Shared.Platform;

/// <summary>
/// 平台 shell 选择器（进程级单例）。所有需要「按平台执行命令 / 按平台比较路径 / 按平台给示例」的模块都从这里取，
/// 不再各自判断 <c>OperatingSystem.IsWindows()</c>。
/// </summary>
public static class PlatformShell
{
    private static readonly Lazy<IShell> Instance = new(Create);
    private static volatile IShell? _override;

    /// <summary>当前平台 shell（测试可通过 <see cref="OverrideForTesting"/> 替换）。</summary>
    public static IShell Current => _override ?? Instance.Value;

    /// <summary>替换当前实现（测试注入用；生产环境请勿调用，传 null 恢复默认）。</summary>
    public static void OverrideForTesting(IShell? shell) => _override = shell;

    private static IShell Create() => PlatformInfo.Current switch
    {
        OsFamily.Windows => new WindowsShell(),
        _ => new PosixShell(),
    };
}
