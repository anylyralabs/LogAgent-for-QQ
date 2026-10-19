using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using LogAgent.Connections;
using LogAgent.Core;
using LogAgent.Knowledge;
using LogAgent.Memory;
using LogAgent.NapCat;
using LogAgent.Routing;
using LogAgent.Sandbox;
using LogAgent.Shared.Chat;
using LogAgent.Shared.Configuration;
using LogAgent.Shared.Messaging;
using LogAgent.Shared.Platform;
using LogAgent.Training;

namespace LogAgent.Headless;

/// <summary>
/// 无头宿主（Linux systemd / Docker / Windows 服务形态）。
/// 用法：
///   LogAgent.Headless                         # 帮助
///   LogAgent.Headless serve                   # 常驻服务（按配置自动连接 OneBot/官方 Bot）
///   LogAgent.Headless providers               # 列出已配置 provider 与规则链
///   LogAgent.Headless chat "你好"             # 走一次 Chat 分流补全
///   LogAgent.Headless import-kb &lt;路径&gt;        # 导入知识库文档（文件或目录，.md/.txt/.log）
///   LogAgent.Headless export chat out.jsonl   # 导出训练数据（chat|sharegpt|alpaca|dpo）
///   LogAgent.Headless config path             # 打印配置目录 / 配置文件路径
///   LogAgent.Headless config repair-sandbox   # 把沙箱白名单/黑名单重置为当前平台默认（先备份）
///   LogAgent.Headless doctor                  # 环境自检（配置/目录/沙箱/MCP/端口）
///   LogAgent.Headless selftest                # 端到端自检（内嵌假模型服务，不联网）
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0) return PrintUsage();

            switch (args[0].ToLowerInvariant())
            {
                case "serve": return await RunServeAsync();
                case "providers": return RunProviders();
                case "chat" when args.Length >= 2: return await RunChatAsync(args[1]);
                case "import-kb" when args.Length >= 2: return await RunImportKbAsync(args[1]);
                case "export" when args.Length >= 2:
                    return RunExport(args[1], args.Length >= 3 ? args[2] : "export.jsonl");
                case "config": return RunConfig(args);
                case "doctor": return RunDoctor();
                case "selftest": return await RunSelftestAsync();
                case "help" or "-h" or "--help": return PrintUsage();
                default: return Unknown(args[0]);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"执行失败：{ex.Message}");
            if (ex.InnerException is { } inner) Console.Error.WriteLine($"  原因：{inner.Message}");
            return 2;
        }
    }

    private static int PrintUsage()
    {
        Console.WriteLine("LogAgent 无头宿主");
        Console.WriteLine($"  平台：{AppConfigStore.DescribePlatform()}");
        Console.WriteLine($"  配置：{AppConfigStore.GetConfigPath()}");
        Console.WriteLine();
        Console.WriteLine("子命令：");
        Console.WriteLine("  serve                    常驻服务（自动连接 OneBot / 官方 Bot）");
        Console.WriteLine("  providers                列出 provider 与分流规则");
        Console.WriteLine("  chat <文本>              走一次 Chat 分流补全");
        Console.WriteLine("  import-kb <路径>         导入知识库文档（文件或目录）");
        Console.WriteLine("  export <格式> [输出路径]  导出训练数据（chat|sharegpt|alpaca|dpo）");
        Console.WriteLine("  config path              打印配置目录与配置文件路径");
        Console.WriteLine("  config repair-sandbox    重置沙箱命令规则为当前平台默认（先备份配置）");
        Console.WriteLine("  doctor                   环境自检");
        Console.WriteLine("  selftest                 端到端自检（内嵌假模型服务，不联网）");
        return 0;
    }

    private static int Unknown(string arg)
    {
        Console.WriteLine($"未知子命令：{arg}");
        PrintUsage();
        return 1;
    }

    // ─────────────────────────── serve ───────────────────────────

    /// <summary>
    /// 常驻服务：装配 <see cref="AgentHost"/>，按配置连接 OneBot11 反向 WS / 官方 Bot / NapCat，
    /// 收到 SIGTERM / SIGINT 时切段会话再退出（systemd 与 docker stop 的默认信号）。
    /// </summary>
    private static async Task<int> RunServeAsync()
    {
        var config = AppConfigStore.Load();
        WarnIfWindowsWhitelist(config);

        Console.WriteLine($"[启动] {AppConfigStore.DescribePlatform()}");
        Console.WriteLine($"[启动] 配置 {AppConfigStore.GetConfigPath()}");

        await using var host = new AgentHost(config);
        host.Logged += line => Console.WriteLine(line);
        host.Ensure();

        using var shutdown = new CancellationTokenSource();
        var signals = new List<PosixSignalRegistration>();
        void OnSignal(PosixSignalContext ctx)
        {
            // 取消默认的「立即终止」行为：由下面的收尾流程切段会话后再退出
            ctx.Cancel = true;
            Console.WriteLine($"[退出] 收到 {ctx.Signal}，开始优雅停止…");
            try { shutdown.Cancel(); } catch (ObjectDisposedException) { /* 已退出 */ }
        }
        if (PlatformInfo.IsUnix)
        {
            signals.Add(PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal));
            signals.Add(PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal));
        }
        else
        {
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                try { shutdown.Cancel(); } catch (ObjectDisposedException) { /* 已退出 */ }
            };
        }

        OneBot11Connection? oneBot = null;
        OfficialBotConnection? officialBot = null;
        var bridge = new ReplyBridge(() => oneBot, () => officialBot);

        if (config.OneBot11.Enabled)
        {
            try
            {
                oneBot = new OneBot11Connection(config.OneBot11);
                oneBot.ConnectionLog += (_, line) => Console.WriteLine(line);
                oneBot.MessageReceived += async (_, e) =>
                {
                    try { await host.Runtime!.OnMessageAsync(bridge, e.Message); }
                    catch (Exception ex) { Console.Error.WriteLine($"[Runtime] 处理消息失败：{ex.Message}"); }
                };
                await oneBot.StartAsync(shutdown.Token);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[OneBot11] 启动失败：{ex.Message}（服务继续运行，其他通道不受影响）");
            }
        }

        if (config.OfficialBot.Enabled)
        {
            try
            {
                officialBot = new OfficialBotConnection(config.OfficialBot);
                officialBot.ConnectionLog += (_, line) => Console.WriteLine(line);
                officialBot.MessageReceived += async (_, e) =>
                {
                    try { await host.Runtime!.OnMessageAsync(bridge, e.Message); }
                    catch (Exception ex) { Console.Error.WriteLine($"[Runtime] 处理消息失败：{ex.Message}"); }
                };
                await officialBot.StartAsync();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[官方Bot] 连接失败：{ex.Message}");
            }
        }

        if (!string.IsNullOrWhiteSpace(config.NapCat.ScriptPath))
        {
            try
            {
                var napCat = new NapCatManager(config.NapCat);
                napCat.ManagerLog += (_, line) => Console.WriteLine(line);
                napCat.InjectReverseWsConfig(config.OneBot11.Port, config.OneBot11.Token);
                if (!napCat.IsRunning) await napCat.StartAsync(shutdown.Token);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[NapCat] 启动失败：{ex.Message}");
            }
        }

        Console.WriteLine("[就绪] 常驻中（Ctrl+C / SIGTERM 退出）");
        try { await Task.Delay(Timeout.InfiniteTimeSpan, shutdown.Token); }
        catch (OperationCanceledException) { /* 正常停机信号 */ }

        // 优雅退出：先切段会话（保住 L1 摘要），再停连接
        await host.CutAllSessionsAsync("shutdown");
        if (oneBot is not null)
        {
            try { await oneBot.StopAsync(); } catch (Exception ex) { Console.Error.WriteLine($"[OneBot11] 停止异常：{ex.Message}"); }
        }
        if (officialBot is not null)
        {
            try { await officialBot.StopAsync(); } catch (Exception ex) { Console.Error.WriteLine($"[官方Bot] 停止异常：{ex.Message}"); }
        }
        foreach (var s in signals) s.Dispose();
        Console.WriteLine("[退出] 完成");
        return 0;
    }

    /// <summary>把 Agent 回复发回当前在线的连接（OneBot 优先，官方 Bot 兜底）。</summary>
    private sealed class ReplyBridge(Func<OneBot11Connection?> oneBot, Func<OfficialBotConnection?> official) : IBotConnectionFactory
    {
        public async Task SendTextAsync(string chatId, ChatType chatType, string text, string? replyToMessageId, CancellationToken ct = default)
        {
            // 优先用「已连入」的通道（OneBot 服务在监听但 NapCat 未连入时，优先走官方 Bot）
            if (oneBot() is { IsConnected: true } conn)
            {
                await conn.SendTextAsync(chatId, chatType, text, replyToMessageId, ct);
                return;
            }
            if (official() is { IsConnected: true } bot)
                await bot.SendTextAsync(chatId, chatType, text, replyToMessageId, ct);
        }

        public async Task<bool> TrySendForwardAsync(string chatId, ChatType chatType, IReadOnlyList<string> sections, CancellationToken ct = default)
        {
            if (oneBot() is { IsConnected: true } conn) return await conn.SendForwardAsync(chatId, chatType, sections, ct);
            return false; // 官方 Bot 无合并转发
        }
    }

    // ─────────────────────────── 既有子命令 ───────────────────────────

    private static async Task<int> RunImportKbAsync(string path)
    {
        var config = AppConfigStore.Load();
        var store = new KnowledgeStore(
            Path.Combine(AppConfigStore.GetDataDirectory(), "knowledge.db"),
            config.Knowledge,
            new ModelRouter(config));
        store.EnsureCreated();
        store.KnowledgeLog += (_, line) => Console.WriteLine($"[知识库] {line}");

        var importer = new KnowledgeImporter(store);
        importer.ImportLog += (_, line) => Console.WriteLine($"[导入] {line}");
        var result = await importer.ImportPathAsync(path);
        Console.WriteLine($"导入完成：{result.Files} 个文件 / {result.Chunks} 条知识，失败 {result.Problems}");
        foreach (var file in result.ProblemFiles)
            Console.WriteLine($"  失败：{file}");
        return result.Chunks > 0 ? 0 : 2;
    }

    private static int RunExport(string formatArg, string outputPath)
    {
        AppConfigStore.Load(); // 保证目录存在
        var db = new Database(Path.Combine(AppConfigStore.GetDataDirectory(), "logagent.db"));
        db.EnsureCreated();
        var format = formatArg.ToLowerInvariant() switch
        {
            "sharegpt" => ExportFormat.ShareGpt,
            "alpaca" => ExportFormat.Alpaca,
            "dpo" => ExportFormat.DpoPair,
            _ => ExportFormat.OpenAiChat,
        };
        var exporter = new TrainingExporter(new SessionStore(db));
        var result = exporter.ExportAsync(outputPath,
            new TrainingExporter.ExportOptions(format, ["none", "adopted"], Redact: true)).Result;
        Console.WriteLine($"导出完成：{result.Path}，样本 {result.Lines} 行（跳过 {result.Skipped}），已脱敏并审计。");
        return 0;
    }

    private static int RunProviders()
    {
        var config = AppConfigStore.Load();
        Console.WriteLine($"Provider（{config.Providers.Count}）：");
        foreach (var p in config.Providers)
            Console.WriteLine($"  [{(p.Enabled ? "启用" : "停用")}] {p.Name} → {p.Endpoint} / {p.ChatModel}");
        Console.WriteLine("分流规则：");
        foreach (var r in config.Rules)
            Console.WriteLine($"  {r.TaskType}: {(r.ProviderChain.Count > 0 ? string.Join(" → ", r.ProviderChain) : "(未配置)")}");
        return 0;
    }

    private static async Task<int> RunChatAsync(string text)
    {
        var config = AppConfigStore.Load();
        var router = new ModelRouter(config);
        router.Metered += (_, e) => Console.WriteLine(
            $"[计量] {e.TaskType}/{e.ProviderId} {(e.Success ? "OK" : $"失败: {e.Error}")} {e.Latency.TotalMilliseconds:F0}ms");
        try
        {
            var result = await router.CompleteAsync(TaskType.Chat, [ChatTurn.User(text)]);
            Console.WriteLine(result.Content);
            Console.WriteLine($"-- {result.Model} · {result.Latency.TotalMilliseconds:F0}ms · {result.PromptTokens}+{result.CompletionTokens} tokens");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"全部 provider 失败：{ex.Message}");
            return 2;
        }
    }

    // ─────────────────────────── config / doctor / selftest ───────────────────────────

    private static int RunConfig(string[] args)
    {
        var sub = args.Length >= 2 ? args[1].ToLowerInvariant() : "";
        switch (sub)
        {
            case "path":
                Console.WriteLine($"平台：{PlatformInfo.Describe()}");
                Console.WriteLine($"配置目录：{AppConfigStore.GetConfigDirectory()}");
                Console.WriteLine($"配置文件：{AppConfigStore.GetConfigPath()}");
                Console.WriteLine($"数据目录：{AppConfigStore.GetDataDirectory()}");
                Console.WriteLine($"文件暂存：{AppConfigStore.GetFilesDirectory()}");
                Console.WriteLine($"沙箱目录：{AppConfigStore.GetSandboxDirectory()}");
                return 0;

            case "repair-sandbox":
                var config = AppConfigStore.Load();
                var backup = AppConfigStore.Backup();
                var before = config.Sandbox.WhitelistCommands.Count;
                config.Sandbox.ResetCommandRulesToPlatformDefaults();
                AppConfigStore.Save(config);
                Console.WriteLine($"已把沙箱命令规则重置为 {PlatformShell.Current.DisplayName} 默认值（原白名单 {before} 条）。");
                Console.WriteLine($"白名单：{string.Join(", ", config.Sandbox.WhitelistCommands)}");
                Console.WriteLine(backup is null ? "（原配置不存在，未备份）" : $"备份：{backup}");
                return 0;

            default:
                Console.WriteLine("用法：config path | config repair-sandbox");
                return 1;
        }
    }

    private static int RunDoctor()
    {
        var ok = true;
        void Check(string name, bool passed, string? detail = null)
        {
            Console.WriteLine($"{(passed ? "✅" : "❌")} {name}{(detail is null ? "" : $"：{detail}")}");
            if (!passed) ok = false;
        }

        Console.WriteLine($"平台：{AppConfigStore.DescribePlatform()}");
        Console.WriteLine();

        var configPath = AppConfigStore.GetConfigPath();
        Check("配置文件存在", File.Exists(configPath), configPath);
        var config = AppConfig.CreateDefault();
        try
        {
            config = AppConfigStore.Load();
            Check("配置可解析", true);
        }
        catch (Exception ex)
        {
            Check("配置可解析", false, ex.Message);
        }

        var writable = true;
        string? writeError = null;
        try
        {
            Directory.CreateDirectory(AppConfigStore.GetDataDirectory());
            var probe = Path.Combine(AppConfigStore.GetDataDirectory(), $".doctor-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            writable = false;
            writeError = ex.Message;
        }
        Check("数据目录可写", writable, writeError ?? AppConfigStore.GetDataDirectory());

        var shell = PlatformShell.Current;
        if (shell is PosixShell)
            Check($"{shell.DisplayName} 可用", File.Exists("/bin/sh"), "/bin/sh");

        var legacy = config.Sandbox.WindowsOnlyCommandsInWhitelist();
        Check("沙箱白名单适配当前平台", legacy.Count == 0,
            legacy.Count == 0 ? null : $"含 Windows 专有命令 {string.Join(", ", legacy)}，可执行 config repair-sandbox");

        foreach (var server in config.Mcp.Servers)
        {
            var found = server.Transport == "stdio" ? FindOnPath(server.Command) : null;
            Console.WriteLine($"   MCP {server.Name}（{server.Transport}）：{(found is null ? "未找到命令" : found)}");
        }

        if (config.OneBot11.Enabled)
        {
            var inUse = IsPortInUse(config.OneBot11.Host, config.OneBot11.Port);
            Console.WriteLine($"   OneBot11 监听 {config.OneBot11.Host}:{config.OneBot11.Port}：{(inUse ? "端口已被占用（可能已有实例）" : "端口空闲（启动时会监听）")}");
        }

        Check("模型 Provider 已配置", config.Providers.Any(p => p.Enabled));
        Console.WriteLine();
        Console.WriteLine(ok ? "自检完成：没有致命问题" : "自检完成：存在需要处理的问题");
        return ok ? 0 : 1;
    }

    private static string? FindOnPath(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        if (command.Contains(Path.DirectorySeparatorChar) || command.Contains('\\') || command.Contains('/'))
            return File.Exists(command) ? Path.GetFullPath(command) : null;

        var extensions = PlatformInfo.IsUnix ? new[] { "" } : new[] { ".exe", ".cmd", ".bat", "" };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            foreach (var ext in extensions)
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim(), command + ext);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { }
            }
        }
        return null;
    }

    private static bool IsPortInUse(string host, int port)
    {
        try
        {
            using var client = new TcpClient();
            var task = client.ConnectAsync(host, port);
            return task.Wait(TimeSpan.FromMilliseconds(400)) && client.Connected;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 端到端自检：启动内嵌假 OpenAI 服务 → 用临时配置目录装配 AgentHost → 沙箱执行一次 →
    /// 本机对话一次 → 断言拿到回复且请求确实到达模型层。全程不联网、不读写用户真实配置。
    /// </summary>
    private static async Task<int> RunSelftestAsync()
    {
        Console.WriteLine("[自检] 启动内嵌假模型服务…");
        await using var stub = EmbeddedOpenAiStub.Start();
        Console.WriteLine($"[自检] 假模型端点：{stub.BaseUrl}");

        // 隔离配置目录，避免污染真实配置；结束后清理
        var tempDir = Path.Combine(Path.GetTempPath(), $"logagent-selftest-{Guid.NewGuid():N}");
        var previous = Environment.GetEnvironmentVariable("LOGAGENT_CONFIG_DIR");
        Environment.SetEnvironmentVariable("LOGAGENT_CONFIG_DIR", tempDir);
        try
        {
            var config = AppConfig.CreateDefault();
            config.Providers =
            [
                new ProviderConfig
                {
                    Id = "selftest",
                    Name = "自检端点",
                    Endpoint = stub.BaseUrl,
                    ApiKey = "selftest",
                    ChatModel = "selftest-model",
                    Enabled = true,
                },
            ];
            foreach (var rule in config.Rules)
                rule.ProviderChain = ["selftest"];
            // Embedding 需要专门的 embeddings 端点；自检端点不提供，留空让路由直接跳过（向量检索降级）
            config.Providers[0].EmbeddingModel = null;
            AppConfigStore.Save(config);

            Console.WriteLine("[自检] 沙箱执行…");
            var runner = new SandboxRunner(new SandboxConfig());
            const string sandboxCommand = "echo selftest-ok"; // echo 在两个平台的默认白名单里都有
            var sandboxResult = await runner.RunAsync(tempDir, sandboxCommand);
            Console.WriteLine($"[自检] 沙箱 exit {sandboxResult.ExitCode} · {sandboxResult.Duration.TotalMilliseconds:F0}ms");
            if (!sandboxResult.Output.Contains("selftest-ok"))
            {
                Console.Error.WriteLine($"[自检] 沙箱执行失败：{sandboxResult.Output}");
                return 1;
            }

            Console.WriteLine("[自检] 装配运行时…");
            await using var host = new AgentHost(AppConfigStore.Load());
            host.Ensure();

            var reply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.LocalChatReply += text =>
            {
                if (!string.IsNullOrWhiteSpace(text)) reply.TrySetResult(text);
            };
            await host.SendLocalChatAsync("自检：请回复正常");
            var completed = await Task.WhenAny(reply.Task, Task.Delay(TimeSpan.FromSeconds(30)));
            if (completed != reply.Task)
            {
                Console.Error.WriteLine("[自检] 30 秒内未收到回复");
                return 1;
            }

            var text = await reply.Task;
            Console.WriteLine($"[自检] 收到回复：{text}");
            if (stub.LastRequestBody is null)
            {
                Console.Error.WriteLine("[自检] 模型端点没有收到请求");
                return 1;
            }
            if (!text.Contains("自检回复"))
            {
                Console.Error.WriteLine("[自检] 回复内容与假模型设定不一致");
                return 1;
            }

            await host.CutAllSessionsAsync("selftest");
            Console.WriteLine("[自检] 通过 ✅");
            return 0;
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOGAGENT_CONFIG_DIR", previous);
            try { Directory.Delete(tempDir, recursive: true); } catch (Exception) { /* 尽力清理 */ }
        }
    }

    /// <summary>Unix 上发现旧 Windows 白名单时提示（不自动改配置）。</summary>
    private static void WarnIfWindowsWhitelist(AppConfig config)
    {
        var legacy = config.Sandbox.WindowsOnlyCommandsInWhitelist();
        if (legacy.Count == 0) return;
        Console.Error.WriteLine(
            $"[提示] 沙箱白名单含 Windows 专有命令（{string.Join(", ", legacy)}），" +
            "在当前平台会被拒绝。执行 `LogAgent.Headless config repair-sandbox` 可重置为平台默认（会先备份配置）。");
    }
}
