using System.Text.RegularExpressions;
using LogAgent.Sandbox;
using LogAgent.Shared.Configuration;
using LogAgent.Shared.Platform;
using Xunit;

namespace LogAgent.Tests;

// ─────────────────────────── 平台抽象层 ───────────────────────────

public class PlatformShellTests
{
    [Fact]
    public void Current_MatchesHostOs()
    {
        var shell = PlatformShell.Current;
        if (OperatingSystem.IsWindows())
        {
            Assert.IsType<WindowsShell>(shell);
            Assert.False(shell.CaseSensitivePaths);
            Assert.Equal("cmd", shell.Id);
        }
        else
        {
            Assert.IsType<PosixShell>(shell);
            Assert.True(shell.CaseSensitivePaths);
            Assert.Equal("sh", shell.Id);
        }
    }

    [Fact]
    public void WindowsShell_WrapsCommandWithUtf8Codepage()
    {
        var spec = new WindowsShell().BuildLaunch("dir");
        Assert.Equal("cmd.exe", spec.FileName);
        Assert.Contains("chcp 65001", spec.Arguments);
        Assert.Contains("dir", spec.Arguments);
        Assert.Equal("utf-8", spec.Environment["PYTHONIOENCODING"]);
    }

    [Fact]
    public void PosixShell_PassesCommandAsSingleArgument()
    {
        var spec = new PosixShell("/bin/sh").BuildLaunch("grep -n \"a b\" x.log");
        Assert.Equal("/bin/sh", spec.FileName);
        Assert.NotNull(spec.ArgumentList);
        Assert.Equal(2, spec.ArgumentList!.Count);
        Assert.Equal("-c", spec.ArgumentList[0]);
        // 整条命令是单个 argv：引号与空格不需要再拼接转义
        Assert.Equal("grep -n \"a b\" x.log", spec.ArgumentList[1]);
        Assert.Equal("C.UTF-8", spec.Environment["LANG"]);
    }

    [Fact]
    public void PlatformInfo_ReportsConsistentFamily()
    {
        if (OperatingSystem.IsWindows())
            Assert.Equal(OsFamily.Windows, PlatformInfo.Current);
        else
            Assert.NotEqual(OsFamily.Windows, PlatformInfo.Current);
        Assert.False(string.IsNullOrWhiteSpace(PlatformInfo.Describe()));
    }
}

public class SandboxConfigPlatformDefaultsTests
{
    [Fact]
    public void DefaultWhitelist_MatchesCurrentPlatform()
    {
        var config = new SandboxConfig();
        var shell = PlatformShell.Current;
        Assert.Equal(shell.DefaultWhitelist.ToArray(), config.WhitelistCommands.ToArray());

        if (OperatingSystem.IsWindows())
            Assert.Contains("findstr", config.WhitelistCommands);
        else
            Assert.DoesNotContain("findstr", config.WhitelistCommands);
    }

    [Fact]
    public void ResetCommandRulesToPlatformDefaults_RestoresBothLists()
    {
        var config = new SandboxConfig
        {
            WhitelistCommands = ["custom-only"],
            BlacklistPatterns = ["never-matches"],
        };
        config.ResetCommandRulesToPlatformDefaults();

        Assert.Equal(PlatformShell.Current.DefaultWhitelist.ToArray(), config.WhitelistCommands.ToArray());
        Assert.Equal(PlatformShell.Current.DefaultBlacklist.ToArray(), config.BlacklistPatterns.ToArray());
        Assert.DoesNotContain("custom-only", config.WhitelistCommands);
    }

    [UnixOnlyFact]
    public void WindowsOnlyCommandsInWhitelist_DetectsLegacyConfig()
    {
        var config = new SandboxConfig { WhitelistCommands = ["findstr", "type", "dir"] };
        var legacy = config.WindowsOnlyCommandsInWhitelist();
        Assert.Equal(3, legacy.Count);
        Assert.Contains("findstr", legacy);

        config.WhitelistCommands = ["grep", "sed"];
        Assert.Empty(config.WindowsOnlyCommandsInWhitelist());
    }

    [Fact]
    public void AllDefaultBlacklistPatterns_Compile()
    {
        foreach (var pattern in PlatformShell.Current.DefaultBlacklist)
            _ = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }
}

public class SandboxPathGuardPlatformTests
{
    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "la-guard-" + Guid.NewGuid().ToString("N")[..8]);

    [Fact]
    public void EnsureInside_CaseSensitivityFollowsPlatform()
    {
        var root = NewRoot() + Path.DirectorySeparatorChar + "CaseRoot";
        var rootFull = Path.GetFullPath(root);
        var differentCase = rootFull.ToUpperInvariant() == rootFull ? rootFull.ToLowerInvariant() : rootFull.ToUpperInvariant();
        var path = Path.Combine(differentCase, "x.log");

        if (PlatformShell.Current.CaseSensitivePaths)
            Assert.Throws<InvalidOperationException>(() => SandboxPathGuard.EnsureInside(root, path));
        else
            Assert.StartsWith(rootFull, SandboxPathGuard.EnsureInside(root, path), StringComparison.OrdinalIgnoreCase);
    }

    [UnixOnlyFact]
    public void EnsureInside_RejectsSymlinkEscape()
    {
        var root = NewRoot();
        var outside = NewRoot() + "-outside";
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "secret");

        var link = Path.Combine(root, "link");
        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // 文件系统不支持符号链接（如某些容器挂载）：跳过
        }

        var ex2 = Assert.Throws<InvalidOperationException>(
            () => SandboxPathGuard.EnsureInside(root, Path.Combine("link", "secret.txt")));
        Assert.Contains("符号链接", ex2.Message);
    }

    [Fact]
    public void EnsureInside_AllowsNormalRelativePath()
    {
        var root = NewRoot();
        var resolved = SandboxPathGuard.EnsureInside(root, "sub/file.log");
        Assert.StartsWith(Path.GetFullPath(root), resolved);
    }
}
