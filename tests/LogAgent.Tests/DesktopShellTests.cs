using Xunit;

namespace LogAgent.Tests;

// ─────────────────────────── 跨平台桌面壳（Avalonia） ───────────────────────────
// 只做静态契约断言（项目属性 / 文件存在 / 不被主解决方案引用），不启动 UI：
// 这样 Windows 与 headless Linux CI 都能跑，且不额外引入 Avalonia.Headless 依赖。

/// <summary>确认桌面壳资源（主题 / 视图 / DataTemplate）可被 XAML 加载。</summary>
public class DesktopShellXamlTests
{
    [Fact]
    public void DesktopProject_IsNotPartOfMainSolution()
    {
        // 桌面壳刻意不加入 slnx：Windows 主构建路径保持零改动
        var slnx = FindRepoFile("LogAgent.slnx");
        Assert.NotNull(slnx);
        var text = File.ReadAllText(slnx!);
        Assert.DoesNotContain("LogAgent.Desktop", text);
        Assert.Contains("LogAgent.App", text); // WPF 壳仍在
    }

    [Fact]
    public void DesktopProject_DeclaresAvaloniaAndSkipsWindowsTfm()
    {
        var csproj = FindRepoFile("src/LogAgent.Desktop/LogAgent.Desktop.csproj");
        Assert.NotNull(csproj);
        var text = File.ReadAllText(csproj!);
        // 必须是跨平台 TFM（不能是 net10.0-windows），否则 Linux 无法构建
        Assert.Contains("<TargetFramework>net10.0</TargetFramework>", text);
        Assert.DoesNotContain("net10.0-windows", text);
        Assert.Contains("Avalonia", text);
    }

    [Fact]
    public void DesktopShell_ReferencesSharedRuntimeInsteadOfDuplicating()
    {
        var csproj = FindRepoFile("src/LogAgent.Desktop/LogAgent.Desktop.csproj");
        Assert.NotNull(csproj);
        var text = File.ReadAllText(csproj!);
        // 复用 AgentHost（LogAgent.Core），不是另起一套运行时
        Assert.Contains(@"..\LogAgent.Core\LogAgent.Core.csproj", text);
        Assert.Contains(@"..\LogAgent.Connections\LogAgent.Connections.csproj", text);
        Assert.Contains(@"..\LogAgent.Sandbox\LogAgent.Sandbox.csproj", text);
    }

    [Fact]
    public void DesktopViews_AreDeclaredForEveryImplementedPage()
    {
        string[] pages = ["DashboardView", "ChatView", "ProvidersView", "LogsView", "SettingsView", "PlaceholderView"];
        foreach (var page in pages)
        {
            Assert.True(FindRepoFile($"src/LogAgent.Desktop/Views/{page}.axaml") is not null, $"缺少视图 {page}.axaml");
            Assert.True(FindRepoFile($"src/LogAgent.Desktop/Views/{page}.axaml.cs") is not null, $"缺少代码隐藏 {page}.axaml.cs");
        }
    }

    /// <summary>从仓库根定位文件：向上找到含 LogAgent.slnx 的目录（不依赖当前工作目录）。</summary>
    private static string? FindRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "LogAgent.slnx")))
            {
                var candidate = Path.Combine(dir.FullName, relativePath);
                return File.Exists(candidate) ? candidate : null;
            }
            dir = dir.Parent;
        }
        return null;
    }
}
