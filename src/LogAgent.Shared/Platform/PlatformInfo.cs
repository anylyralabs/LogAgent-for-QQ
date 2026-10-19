namespace LogAgent.Shared.Platform;

/// <summary>操作系统族（适配层只区分这三类，其余细节由各自的平台实现决定）。</summary>
public enum OsFamily
{
    Windows,
    Linux,
    MacOS,
}

/// <summary>
/// 运行时平台信息：统一判定入口，避免各模块重复写 <c>OperatingSystem.IsWindows()</c>。
/// 注意：这里「Linux」也包含 Android（Termux/自建容器），需要区分时用 <see cref="IsAndroid"/>。
/// </summary>
public static class PlatformInfo
{
    private static readonly Lazy<OsFamily> CurrentLazy = new(Detect);

    /// <summary>当前平台。</summary>
    public static OsFamily Current => CurrentLazy.Value;

    /// <summary>非 Windows（Linux / macOS / Android）。</summary>
    public static bool IsUnix => Current != OsFamily.Windows;

    /// <summary>Android（用于提示：应用数据目录与可执行权限规则与桌面 Linux 不同）。</summary>
    public static bool IsAndroid =>
        Current == OsFamily.Linux && File.Exists("/system/build.prop");

    /// <summary>疑似运行在容器内（仅用于 doctor/日志提示，不改变任何行为）。</summary>
    public static bool IsContainerized =>
        IsUnix && (File.Exists("/.dockerenv")
                   || File.Exists("/run/.containerenv")
                   || (File.Exists("/proc/1/cgroup") && File.ReadAllText("/proc/1/cgroup").Contains("docker", StringComparison.Ordinal)));

    /// <summary>人类可读的平台描述（日志 / doctor 用）。</summary>
    public static string Describe()
    {
        var os = Current switch
        {
            OsFamily.Windows => "Windows",
            OsFamily.MacOS => "macOS",
            _ => IsAndroid ? "Linux (Android)" : "Linux",
        };
        return IsContainerized ? $"{os} (container)" : os;
    }

    private static OsFamily Detect()
    {
        if (OperatingSystem.IsWindows()) return OsFamily.Windows;
        if (OperatingSystem.IsMacOS()) return OsFamily.MacOS;
        return OsFamily.Linux;
    }
}
