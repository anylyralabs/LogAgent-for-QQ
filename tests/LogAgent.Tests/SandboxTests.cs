using LogAgent.Sandbox;
using Xunit;

namespace LogAgent.Tests;

public class SandboxPathGuardTests
{
    private static string Root() => Path.Combine(Path.GetTempPath(), "la-test-" + Guid.NewGuid().ToString("N")[..8]);

    [Theory]
    [InlineData("a.txt")]
    [InlineData("dir/sub/b.log")]
    [InlineData("./c.txt")]
    public void EnsureInside_AcceptsSandboxRelative(string relative)
    {
        var root = Root();
        var resolved = SandboxPathGuard.EnsureInside(root, relative);
        Assert.StartsWith(Path.GetFullPath(root), resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("dir/../../escape.txt")]
    public void EnsureInside_RejectsTraversal(string path)
    {
        var root = Root();
        Assert.Throws<InvalidOperationException>(() => SandboxPathGuard.EnsureInside(root, path));
    }

    [Fact]
    public void EnsureInside_RejectsAbsoluteOutsidePath()
    {
        var root = Root();
        var outside = Path.Combine(Path.GetTempPath(), "outside.txt");
        Assert.Throws<InvalidOperationException>(() => SandboxPathGuard.EnsureInside(root, outside));
    }

    [Fact]
    public void IsInside_ReturnsFalseWithoutThrowing()
    {
        var root = Root();
        Assert.False(SandboxPathGuard.IsInside(root, "../x"));
        Assert.True(SandboxPathGuard.IsInside(root, "x"));
    }
}

public class SandboxWorkspaceTests
{
    [Fact]
    public void Workspace_CreatesDirectory_AndListsFiles()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "la-sb-" + Guid.NewGuid().ToString("N")[..8]);
        using (var ws = new SandboxWorkspace(baseDir, "task1"))
        {
            File.WriteAllText(ws.Resolve("hello.log"), "line1");
            Directory.CreateDirectory(ws.Resolve("sub"));
            File.WriteAllText(ws.Resolve("sub/deep.log"), "line2");

            var files = ws.ListFiles();
            Assert.Contains("hello.log", files);
            Assert.Contains(Path.Combine("sub", "deep.log").Replace('\\', '/'), files.Select(f => f.Replace('\\', '/')));
        }
        Assert.False(Directory.Exists(Path.Combine(baseDir, "task1"))); // Dispose 清理
    }
}
