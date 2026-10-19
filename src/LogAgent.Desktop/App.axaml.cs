using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using LogAgent.Desktop.Services;
using LogAgent.Desktop.ViewModels;
using LogAgent.Desktop.Views;

namespace LogAgent.Desktop;

/// <summary>
/// 跨平台桌面壳入口（Windows / Linux / macOS）。
/// 与 WPF 壳共用 <see cref="LogAgent.Core.AgentHost"/> 运行时：同一套会话 / 知识库 / 沙箱 / 技能 / MCP 管线。
/// </summary>
public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // 未处理异常兜底：写入数据目录下的 desktop-crash.log，便于无终端环境排查
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 单实例：已运行则唤醒已有窗口后退出（托盘的替代品：Linux 无托盘时退化为单窗口）
            if (!SingleInstance.TryAcquire())
            {
                SingleInstance.RequestShow();
                desktop.Shutdown();
                return;
            }

            var shell = new ShellViewModel();
            desktop.MainWindow = new MainWindow { DataContext = shell };
            desktop.ShutdownRequested += async (_, _) =>
            {
                await shell.ShutdownAsync();
                SingleInstance.Release();
            };
            SingleInstance.ListenForShowRequest(() => desktop.MainWindow?.Activate());
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void LogCrash(Exception? ex)
    {
        try
        {
            var path = Path.Combine(LogAgent.Shared.Configuration.AppConfigStore.GetConfigDirectory(), "desktop-crash.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path,
                $"[{DateTimeOffset.Now:O}] {ex?.GetType().Name}: {ex?.Message}{Environment.NewLine}{ex?.StackTrace}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // 崩溃日志本身失败时无计可施
        }
    }
}
