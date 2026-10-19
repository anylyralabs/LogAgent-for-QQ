using System.Runtime.Versioning;

namespace LogAgent.Desktop.Services;

/// <summary>
/// 桌面壳的平台服务：开机自启、打开目录/链接、托盘能力探测。
/// Windows 走注册表与 ShellExecute；Linux 走 XDG autostart 与 xdg-open。
/// </summary>
public static class PlatformService
{
    /// <summary>是否为 Windows（决定部分 UI 文案）。</summary>
    public static bool IsWindows => OperatingSystem.IsWindows();

    // ─────────────────────────── 开机自启 ───────────────────────────

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LogAgent";

    private static string DesktopEntryPath()
    {
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configHome))
            configHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(configHome, "autostart", "logagent.desktop");
    }

    public static bool IsAutoStartEnabled() =>
        OperatingSystem.IsWindows() ? IsAutoStartEnabledWindows() : File.Exists(DesktopEntryPath());

    public static bool SetAutoStart(bool enabled) =>
        OperatingSystem.IsWindows() ? SetAutoStartWindows(enabled) : SetAutoStartUnix(enabled);

    [SupportedOSPlatform("windows")]
    private static bool IsAutoStartEnabledWindows()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
        catch (Exception)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool SetAutoStartWindows(bool enabled)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return false;
                key.SetValue(ValueName, $"\"{exe}\" --minimized");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool SetAutoStartUnix(bool enabled)
    {
        var path = DesktopEntryPath();
        try
        {
            if (!enabled)
            {
                if (File.Exists(path)) File.Delete(path);
                return true;
            }

            var exePath = Environment.ProcessPath ?? "dotnet";
            var exec = Path.GetFileNameWithoutExtension(exePath)
                           .Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                ? $"dotnet \"{Path.Combine(AppContext.BaseDirectory, "LogAgent.Desktop.dll")}\" --minimized"
                : $"\"{exePath}\" --minimized";

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, $"""
                [Desktop Entry]
                Type=Application
                Name=LogAgent
                Comment=QQ 日志分析助手
                Exec={exec}
                Terminal=false
                X-GNOME-Autostart-enabled=true
                """);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>开机自启文案（设置页）。</summary>
    public static string AutoStartLabel => OperatingSystem.IsWindows()
        ? "开机自启动（登录 Windows 后自动启动）"
        : "开机自启动（登录桌面后自动启动）";

    // ─────────────────────────── 打开目录 / 链接 ───────────────────────────

    /// <summary>用系统默认程序打开目录或 URL（失败返回错误信息）。</summary>
    public static string? Open(string pathOrUrl)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(pathOrUrl)
                {
                    UseShellExecute = true,
                });
                return null;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("xdg-open")
            {
                ArgumentList = { pathOrUrl },
                UseShellExecute = false,
            });
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>当前桌面环境是否支持系统托盘（Linux 需要 StatusNotifier / D-Bus 会话）。</summary>
    public static bool TrayLikelySupported =>
        OperatingSystem.IsWindows()
        || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS"));
}
