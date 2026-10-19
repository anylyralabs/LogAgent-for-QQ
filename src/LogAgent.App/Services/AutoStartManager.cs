using System.Text.Json;
using LogAgent.Shared.Configuration;
using LogAgent.Shared.Platform;

namespace LogAgent.App.Services;

/// <summary>
/// 开机自启动：Windows 走 HKCU\Run（免管理员）；Linux/macOS 写 ~/.config/autostart/*.desktop。
/// </summary>
public static class AutoStartManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LogAgent";

    public static bool IsEnabled() => PlatformInfo.Current == OsFamily.Windows ? IsEnabledWindows() : IsEnabledDesktop();

    /// <summary>启用/禁用开机自启动（启用时带 --minimized 参数后台启动）。</summary>
    public static bool SetEnabled(bool enabled) =>
        PlatformInfo.Current == OsFamily.Windows ? SetEnabledWindows(enabled) : SetEnabledDesktop(enabled);

    // ---------- Windows ----------

    private static bool IsEnabledWindows()
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

    private static bool SetEnabledWindows(bool enabled)
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

    // ---------- Linux / macOS（XDG autostart） ----------

    private static string DesktopFilePath()
    {
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configHome))
            configHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(configHome, "autostart", "logagent.desktop");
    }

    private static bool IsEnabledDesktop() => File.Exists(DesktopFilePath());

    private static bool SetEnabledDesktop(bool enabled)
    {
        var path = DesktopFilePath();
        try
        {
            if (!enabled)
            {
                if (File.Exists(path)) File.Delete(path);
                return true;
            }

            var exe = Environment.ProcessPath ?? "dotnet";
            var execLine = exe.Equals("dotnet", StringComparison.Ordinal)
                ? $"dotnet \"{Path.Combine(AppContext.BaseDirectory, "LogAgent.App.dll")}\" --minimized"
                : $"\"{exe}\" --minimized";

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, $"""
                [Desktop Entry]
                Type=Application
                Name=LogAgent
                Comment=QQ 日志分析助手
                Exec={execLine}
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

    /// <summary>当前平台的启动项说明（设置页展示用）。</summary>
    public static string Describe() => PlatformInfo.Current == OsFamily.Windows
        ? "开机自启动（登录 Windows 后自动后台启动）"
        : $"开机自启动（登录桌面后自动启动，写入 {DesktopFilePath()}）";
}
