using Avalonia;

namespace LogAgent.Desktop;

/// <summary>程序入口（Avalonia 标准引导），与 App.axaml 一起构成桌面壳。</summary>
internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() => AppBuilder
        .Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
