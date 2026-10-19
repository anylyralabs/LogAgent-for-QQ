using LogAgent.Core;
using LogAgent.Routing;
using LogAgent.Shared.Configuration;
using Xunit;

namespace LogAgent.Tests;

// ─────────────────────────── AgentHost 装配（无 UI 组合根） ───────────────────────────

/// <summary>共享集合：这些用例会改进程级 LOGAGENT_CONFIG_DIR，必须串行。</summary>
[CollectionDefinition("config-dir-env")]
public sealed class ConfigDirEnvCollection;

[Collection("config-dir-env")]
public class AgentHostTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"la-host-{Guid.NewGuid():N}");
    private readonly string? _previousConfigDir;

    public AgentHostTests()
    {
        _previousConfigDir = Environment.GetEnvironmentVariable("LOGAGENT_CONFIG_DIR");
        Environment.SetEnvironmentVariable("LOGAGENT_CONFIG_DIR", _tempDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("LOGAGENT_CONFIG_DIR", _previousConfigDir);
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }

    private static ModelRouter MakeRouter()
    {
        var config = new AppConfig
        {
            Providers = [new ProviderConfig { Id = "p1", Name = "P1", ChatModel = "m", Enabled = true }],
            Rules = Enum.GetValues<TaskType>()
                .Select(t => new RouterRule { TaskType = t, ProviderChain = ["p1"] })
                .ToList(),
        };
        return new ModelRouter(config, _ => new FakeClient("p1", true, "本机回复"));
    }

    [Fact]
    public async Task Ensure_BuildsRuntimeAndStores()
    {
        var host = new AgentHost(AppConfigStore.Load(), MakeRouter());
        host.Ensure();

        Assert.True(host.IsReady);
        Assert.NotNull(host.Runtime);
        Assert.NotNull(host.Sessions);
        Assert.NotNull(host.Sandbox);
        Assert.NotNull(host.Knowledge);
        Assert.NotNull(host.Skills);
        Assert.NotNull(host.Mcp);
        // 数据目录在隔离的临时配置目录下创建
        Assert.StartsWith(Path.GetFullPath(_tempDir), Path.GetFullPath(AppConfigStore.GetDataDirectory()));
        Assert.True(Directory.Exists(AppConfigStore.GetDataDirectory()));

        await host.DisposeAsync();
    }

    [Fact]
    public async Task Ensure_IsIdempotent()
    {
        var host = new AgentHost(AppConfigStore.Load(), MakeRouter());
        host.Ensure();
        var sessions = host.Sessions;
        host.Ensure();
        Assert.Same(sessions, host.Sessions);
        await host.DisposeAsync();
    }

    [Fact]
    public async Task SendLocalChat_DeliversReplyThroughEvent()
    {
        var host = new AgentHost(AppConfigStore.Load(), MakeRouter());
        host.Ensure();

        var reply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.LocalChatReply += text => reply.TrySetResult(text);
        await host.SendLocalChatAsync("你好");

        var finished = await Task.WhenAny(reply.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.Same(reply.Task, finished);
        Assert.Contains("本机回复", await reply.Task);

        await host.DisposeAsync();
    }

    [Fact]
    public async Task CutAllSessions_DoesNotThrowWhenEmpty()
    {
        var host = new AgentHost(AppConfigStore.Load(), MakeRouter());
        host.Ensure();
        await host.CutAllSessionsAsync("test");
        await host.CutAllSessionsAsync("test-again");
        await host.DisposeAsync();
    }
}

[Collection("config-dir-env")]
public class AppConfigStoreTests
{
    [Fact]
    public void ConfigDirectory_HonorsEnvironmentOverride()
    {
        var previous = Environment.GetEnvironmentVariable("LOGAGENT_CONFIG_DIR");
        var custom = Path.Combine(Path.GetTempPath(), $"la-cfg-{Guid.NewGuid():N}");
        try
        {
            Environment.SetEnvironmentVariable("LOGAGENT_CONFIG_DIR", custom);
            Assert.Equal(Path.GetFullPath(custom), AppConfigStore.GetConfigDirectory());
            Assert.Equal(Path.Combine(Path.GetFullPath(custom), "config.json"), AppConfigStore.GetConfigPath());
            Assert.Equal(Path.Combine(Path.GetFullPath(custom), "data"), AppConfigStore.GetDataDirectory());
            Assert.Equal(Path.Combine(Path.GetFullPath(custom), "files"), AppConfigStore.GetFilesDirectory());
            Assert.Equal(Path.Combine(Path.GetFullPath(custom), "sandbox"), AppConfigStore.GetSandboxDirectory());
            Assert.Equal(Path.Combine(Path.GetFullPath(custom), "skills"), AppConfigStore.GetSkillsDirectory());
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOGAGENT_CONFIG_DIR", previous);
        }
    }

    [Fact]
    public void ConfigDirectory_DefaultsToPlatformAppData()
    {
        var previous = Environment.GetEnvironmentVariable("LOGAGENT_CONFIG_DIR");
        try
        {
            Environment.SetEnvironmentVariable("LOGAGENT_CONFIG_DIR", null);
            var dir = AppConfigStore.GetConfigDirectory();
            Assert.EndsWith("LogAgent", dir);
            // Linux 下 XDG 默认 ~/.config/LogAgent；Windows 下 %AppData%\LogAgent
            if (!OperatingSystem.IsWindows())
                Assert.Contains(".config", dir);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOGAGENT_CONFIG_DIR", previous);
        }
    }

    [Fact]
    public void SaveAndLoad_RoundTripsSandboxLists()
    {
        var previous = Environment.GetEnvironmentVariable("LOGAGENT_CONFIG_DIR");
        var temp = Path.Combine(Path.GetTempPath(), $"la-cfg-{Guid.NewGuid():N}");
        try
        {
            Environment.SetEnvironmentVariable("LOGAGENT_CONFIG_DIR", temp);
            var config = AppConfig.CreateDefault();
            config.Sandbox.WhitelistCommands = ["custom-cmd"];
            AppConfigStore.Save(config);

            var loaded = AppConfigStore.Load();
            Assert.Contains("custom-cmd", loaded.Sandbox.WhitelistCommands);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOGAGENT_CONFIG_DIR", previous);
            try { Directory.Delete(temp, recursive: true); } catch (IOException) { }
        }
    }
}
