using LogAgent.Core;
using LogAgent.Routing;
using LogAgent.Shared.Chat;
using LogAgent.Shared.Configuration;
using Xunit;

namespace LogAgent.Tests;

// ─────────────────────────── 日志分析：文件模式（用工具查看完整文件） ───────────────────────────

public class LogAnalysisFileModeTests : IDisposable
{
    private readonly string _filesDir = Path.Combine(Path.GetTempPath(), $"la-files-{Guid.NewGuid():N}");

    public LogAnalysisFileModeTests() => Directory.CreateDirectory(_filesDir);

    public void Dispose()
    {
        try { Directory.Delete(_filesDir, true); } catch (IOException) { }
    }

    private static AppConfig MakeConfig() => new()
    {
        Providers = [new ProviderConfig { Id = "p1", Name = "P1", ChatModel = "m", Enabled = true }],
        Rules =
        [
            new RouterRule { TaskType = TaskType.LogAnalysis, ProviderChain = ["p1"] },
            new RouterRule { TaskType = TaskType.DeepReasoning, ProviderChain = ["p1"] },
        ],
    };

    [Fact]
    public async Task StagedFile_UsesToolLoopAndReports()
    {
        var logPath = Path.Combine(_filesDir, "20261015-000000-latest_game.log");
        await File.WriteAllTextAsync(logPath, "line1\nERROR: SIGSEGV crash here\nline3");

        var host = new RecordingToolHost();
        var calls = 0;
        FakeClient? client = null;
        var router = new ModelRouter(MakeConfig(), _ => client = new FakeClient("p1", true)
        {
            ResultOverride = _ =>
            {
                calls++;
                return calls switch
                {
                    1 => new ChatResult("", "p1", "m", 1, 1, TimeSpan.Zero,
                        [new ChatToolCall("c1", "demo_tool", """{"path":"latest_game.log","pattern":"SIGSEGV"}""")],
                        "tool_calls"),
                    2 => new ChatResult("根因分析结论：崩溃由渲染器引起（证据 L2）", "p1", "m", 1, 1, TimeSpan.Zero),
                    _ => new ChatResult("PASS 结论有证据支撑", "p1", "m", 1, 1, TimeSpan.Zero),
                };
            },
        });

        var agent = new LogAnalysisAgent(router, null, null, host, new SkillsConfig(), _filesDir);
        var reply = await agent.AnalyzeAsync($"请分析这个日志\n本机路径：{logPath}");

        Assert.Contains("日志分析报告（文件", reply);
        Assert.Contains("根因分析", reply);
        Assert.Contains("渲染器", reply);
        // 模型真实发起了工具调用（而不是只凭消息文本作答）
        var invoked = Assert.Single(host.Invoked);
        Assert.Equal("demo_tool", invoked.Name);
    }

    [Fact]
    public async Task PathOutsideFilesDir_FallsBackToTextMode()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}.log");
        await File.WriteAllTextAsync(outside, "x");
        try
        {
            var host = new RecordingToolHost();
            FakeClient? client = null;
            var router = new ModelRouter(MakeConfig(), _ => client = new FakeClient("p1", true, reply: "ok"));

            var agent = new LogAnalysisAgent(router, null, null, host, new SkillsConfig(), _filesDir);
            var reply = await agent.AnalyzeAsync($"请分析这个日志\nERROR: something failed\n本机路径：{outside}");

            Assert.Contains("日志分析报告", reply);
            Assert.DoesNotContain("（文件：", reply); // 未走文件模式
            Assert.Empty(host.Invoked);               // 不应发起任何工具调用
        }
        finally
        {
            try { File.Delete(outside); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task StagedFile_WithoutTools_DegradesToSampleAnalysis()
    {
        var logPath = Path.Combine(_filesDir, "sample.log");
        await File.WriteAllTextAsync(logPath, "head\nERROR: boom in sample\nstack at x.y.Z");

        FakeClient? client = null;
        var router = new ModelRouter(MakeConfig(), _ => client = new FakeClient("p1", true, reply: "分析: 样本中发现 ERROR（证据行 2）"));
        var agent = new LogAnalysisAgent(router, null, null, null, null, _filesDir); // 无工具宿主

        var reply = await agent.AnalyzeAsync($"分析日志\n本机路径：{logPath}");

        Assert.Contains("日志分析报告", reply);
        Assert.Contains("样本中发现 ERROR", reply);
    }
}

// ─────────────────────────── 工具标记清理（收尾调用防泄漏） ───────────────────────────

public class ToolMarkupTests
{
    [Fact]
    public void Strip_CutsLeakedToolCallMarkup()
    {
        var leaked = "先确认一下 mod 列表。<tool_calls:opensource><tool_call:opensource>read_file"
            + "<arg_key:opensource>path</arg_key:opensource><arg_value:opensource>x.log</arg_value:opensource>"
            + "</tool_call:opensource></tool_calls:opensource>";
        Assert.Equal("先确认一下 mod 列表。", LogAgent.Core.ToolMarkup.Strip(leaked));

        Assert.Equal("正常文本没有标记", LogAgent.Core.ToolMarkup.Strip("正常文本没有标记"));
        Assert.Equal("", LogAgent.Core.ToolMarkup.Strip(""));
    }
}
