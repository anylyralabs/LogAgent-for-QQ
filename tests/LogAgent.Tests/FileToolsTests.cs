using LogAgent.Sandbox;
using LogAgent.Shared.Configuration;
using LogAgent.Skills;
using Xunit;

namespace LogAgent.Tests;

// ─────────────────────────── 内置文件工具（read_file / search_file / run_shell） ───────────────────────────

public class FileToolsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"la-files-{Guid.NewGuid():N}");

    public FileToolsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private FileTools Make(SandboxRunner? runner = null) => new(_dir, runner);

    private async Task<string> Run(FileTools tools, string tool, string args)
        => await tools.ExecuteAsync(tool, args);

    // ---------- read_file ----------

    [Fact]
    public async Task ReadFile_RangeWithLineNumbers()
    {
        File.WriteAllLines(Path.Combine(_dir, "n.log"), Enumerable.Range(1, 100).Select(i => $"line-{i}"));
        var output = await Run(Make(), FileTools.ReadFileTool, """{"path":"n.log","offset":10,"limit":5}""");

        Assert.Contains("共 100 行", output);
        Assert.Contains("显示 10-14 行", output);
        Assert.Contains("10 | line-10", output);
        Assert.Contains("14 | line-14", output);
        Assert.DoesNotContain("15 | line-15", output);
    }

    [Fact]
    public async Task ReadFile_TailViaOffset()
    {
        File.WriteAllLines(Path.Combine(_dir, "n.log"), Enumerable.Range(1, 100).Select(i => $"line-{i}"));
        var output = await Run(Make(), FileTools.ReadFileTool, """{"path":"n.log","offset":99,"limit":10}""");

        Assert.Contains("99 | line-99", output);
        Assert.Contains("100 | line-100", output);
    }

    [Fact]
    public async Task ReadFile_OffsetBeyondEnd_HintsTailOffset()
    {
        File.WriteAllLines(Path.Combine(_dir, "n.log"), Enumerable.Range(1, 10).Select(i => $"line-{i}"));
        var output = await Run(Make(), FileTools.ReadFileTool, """{"path":"n.log","offset":200}""");

        Assert.Contains("超出末尾", output);
        Assert.Contains("offset=", output);
    }

    [Fact]
    public async Task ReadFile_PathOutsideFilesDir_Refused()
    {
        var output = await Run(Make(), FileTools.ReadFileTool, """{"path":"C:/Windows/win.ini"}""");
        Assert.Contains("只能访问 files 目录", output);
    }

    [Fact]
    public async Task ReadFile_MissingFile_Explains()
    {
        var output = await Run(Make(), FileTools.ReadFileTool, """{"path":"nope.log"}""");
        Assert.Contains("文件不存在", output);
    }

    // ---------- search_file ----------

    [Fact]
    public async Task SearchFile_FindsWithContextAndMarkers()
    {
        File.WriteAllText(Path.Combine(_dir, "t.log"),
            "alpha\nbeta\nException: boom happened\ncontext-a\nafter1\nafter2\nomega");
        var output = await Run(Make(), FileTools.SearchFileTool, """{"path":"t.log","pattern":"Exception","context":1}""");

        Assert.Contains("命中 1 处", output);
        Assert.Contains("3: > Exception: boom happened", output);  // 命中行带 > 标记
        Assert.Contains("2:   beta", output);                      // 上文
        Assert.Contains("4:   context-a", output);                 // 下文
    }

    [Fact]
    public async Task SearchFile_RegexWorks()
    {
        File.WriteAllText(Path.Combine(_dir, "t.log"), "aaa\nException in thread main\nbbb");
        var output = await Run(Make(), FileTools.SearchFileTool, """{"path":"t.log","pattern":"Exception.*main","context":0}""");
        Assert.Contains("命中 1 处", output);
    }

    [Fact]
    public async Task SearchFile_InvalidRegex_FallsBackToLiteral()
    {
        File.WriteAllText(Path.Combine(_dir, "t.log"), "safe\na(b literal here\nsafe2");
        var output = await Run(Make(), FileTools.SearchFileTool, """{"path":"t.log","pattern":"a(b"}""");
        Assert.Contains("命中 1 处", output);
        Assert.Contains("a(b literal here", output);
    }

    [Fact]
    public async Task SearchFile_ChineseKeyword()
    {
        File.WriteAllText(Path.Combine(_dir, "t.log"), "加载中\n资源加载失败：纹理缺失\n完成");
        var output = await Run(Make(), FileTools.SearchFileTool, """{"path":"t.log","pattern":"加载失败"}""");
        Assert.Contains("命中 1 处", output);
        Assert.Contains("资源加载失败", output);
    }

    [Fact]
    public async Task SearchFile_NoMatch_GivesHint()
    {
        File.WriteAllText(Path.Combine(_dir, "t.log"), "nothing here");
        var output = await Run(Make(), FileTools.SearchFileTool, """{"path":"t.log","pattern":"zzz_not_found"}""");
        Assert.Contains("未命中", output);
        Assert.Contains("建议", output);
    }

    [Fact]
    public async Task SearchFile_MaxMatches_Truncates()
    {
        File.WriteAllLines(Path.Combine(_dir, "t.log"), Enumerable.Range(1, 50).Select(i => $"hit-{i}"));
        var output = await Run(Make(), FileTools.SearchFileTool, """{"path":"t.log","pattern":"hit","max_matches":10,"context":0}""");
        Assert.Contains("命中 10 处", output);
        Assert.Contains("已截断", output);
    }

    // ---------- run_shell（沙箱） ----------

    [Fact]
    public async Task RunShell_SearchWorks()
    {
        File.WriteAllText(Path.Combine(_dir, "t.log"), "line1\nneedle here\nline3");
        var runner = new SandboxRunner(new SandboxConfig());
        // 按平台选择白名单内可用的搜索命令（Windows: findstr / Linux: grep）
        var command = OperatingSystem.IsWindows()
            ? "findstr /n \"needle\" t.log"
            : "grep -n \"needle\" t.log";
        var json = System.Text.Json.JsonSerializer.Serialize(new { command });
        var output = await Run(Make(runner), FileTools.RunShellTool, json);

        Assert.Contains("needle here", output);
        Assert.Contains("exit 0", output);
    }

    [UnixOnlyFact]
    public async Task RunShell_TimeoutKillsProcessTree_OnUnix()
    {
        // 超时与杀进程树依赖 /proc，必须在真实 Unix 上验证（Windows 走 Job Object 语义）
        // 超时下限被钳到 5 秒（SandboxRunner 里 Math.Max(5, ...)），所以用 5 秒 + sleep 30
        var config = new SandboxConfig
        {
            CommandTimeoutSeconds = 5,
            WhitelistCommands = ["sleep"],
            BlacklistPatterns = [],
        };
        var runner = new SandboxRunner(config);
        var output = await Run(Make(runner), FileTools.RunShellTool, """{"command":"sleep 30"}""");

        Assert.Contains("超时", output);
    }

    [Fact]
    public async Task RunShell_BlacklistedCommand_Refused()
    {
        var runner = new SandboxRunner(new SandboxConfig());
        var command = OperatingSystem.IsWindows() ? "shutdown /s" : "sudo rm -rf /";
        var json = System.Text.Json.JsonSerializer.Serialize(new { command });
        var output = await Run(Make(runner), FileTools.RunShellTool, json);
        Assert.Contains("拒绝", output);
    }

    [Fact]
    public async Task RunShell_NotWhitelisted_Refused()
    {
        var runner = new SandboxRunner(new SandboxConfig());
        var output = await Run(Make(runner), FileTools.RunShellTool, """{"command":"curl http://example.com"}""");
        Assert.Contains("白名单", output);
    }

    [Fact]
    public async Task RunShell_NoRunner_ExplainsDisabled()
    {
        var output = await Run(Make(), FileTools.RunShellTool, """{"command":"dir"}""");
        Assert.Contains("未启用", output);
    }

    // ---------- 工具定义 ----------

    [Fact]
    public void Definitions_IncludeBuiltins_ShellGated()
    {
        var runner = new SandboxRunner(new SandboxConfig());
        var withShell = Make(runner).BuildToolDefinitions(shellEnabled: true);
        Assert.Contains(withShell, d => d.Name == FileTools.ReadFileTool);
        Assert.Contains(withShell, d => d.Name == FileTools.SearchFileTool);
        Assert.Contains(withShell, d => d.Name == FileTools.RunShellTool);

        var withoutShell = Make(runner).BuildToolDefinitions(shellEnabled: false);
        Assert.DoesNotContain(withoutShell, d => d.Name == FileTools.RunShellTool);
    }

    [Fact]
    public async Task Hub_RoutesBuiltinTools()
    {
        var skillsDir = Path.Combine(_dir, "skills");
        Directory.CreateDirectory(skillsDir);
        var registry = new SkillRegistry(skillsDir, Path.Combine(skillsDir, "user"));
        registry.Reload();
        var tools = new FileTools(_dir);
        var hub = new ToolHostHub(registry, null, new AppConfig(), tools);

        var defs = hub.BuildToolDefinitions(new SkillsConfig());
        Assert.Contains(defs, d => d.Name == FileTools.ReadFileTool);

        File.WriteAllText(Path.Combine(_dir, "x.log"), "hello from hub");
        var output = await hub.ExecuteToolAsync(FileTools.ReadFileTool, """{"path":"x.log"}""", new SkillsConfig());
        Assert.Contains("hello from hub", output);
    }

    // ---------- 清理 ----------

    [Fact]
    public void CleanupOldFiles_RemovesExpiredOnly()
    {
        var oldFile = Path.Combine(_dir, "old.log");
        var newFile = Path.Combine(_dir, "new.log");
        File.WriteAllText(oldFile, "old");
        File.WriteAllText(newFile, "new");
        File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddDays(-30));

        var removed = FileTools.CleanupOldFiles(_dir, TimeSpan.FromDays(7));

        Assert.Equal(1, removed);
        Assert.False(File.Exists(oldFile));
        Assert.True(File.Exists(newFile));
    }
}
