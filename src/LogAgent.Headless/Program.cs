using LogAgent.Knowledge;
using LogAgent.Memory;
using LogAgent.Routing;
using LogAgent.Shared.Chat;
using LogAgent.Shared.Configuration;
using LogAgent.Training;

namespace LogAgent.Headless;

/// <summary>
/// 无头宿主（Linux systemd/Docker 形态，规划第 9 节）。
/// 用法：
///   LogAgent.Headless                     # 常驻模式（占位，后续接连接层）
///   LogAgent.Headless providers           # 列出已配置 provider 与规则链
///   LogAgent.Headless chat "你好"         # 走一次 Chat 分流补全
///   LogAgent.Headless import-kb <路径>    # 导入知识库文档（文件或目录，.md/.txt/.log）
///   LogAgent.Headless export chat out.jsonl  # 导出训练数据（chat|sharegpt|alpaca|dpo）
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var config = AppConfigStore.Load();
        var db = new Database(Path.Combine(AppConfigStore.GetConfigDirectory(), "data", "logagent.db"));
        db.EnsureCreated();

        if (args.Length == 0)
        {
            Console.WriteLine("LogAgent 无头模式：常驻服务将在连接层接入后启用。");
            Console.WriteLine($"配置: {AppConfigStore.GetConfigPath()}");
            Console.WriteLine($"会话库: {db.DbPath}");
            Console.WriteLine("子命令：providers | chat <文本> | import-kb <路径> | export <格式> <输出路径>");
            return 0;
        }

        switch (args[0].ToLowerInvariant())
        {
            case "providers":
                PrintProviders(config);
                return 0;

            case "chat" when args.Length >= 2:
                return await RunChatAsync(config, args[1]);

            case "import-kb" when args.Length >= 2:
                return await RunImportKbAsync(config, args[1]);

            case "export" when args.Length >= 3:
                return RunExport(db, args[1], args[2]);

            default:
                Console.WriteLine($"未知子命令：{args[0]}");
                return 1;
        }
    }

    /// <summary>导入知识库文档（与 App 端「导入文档」走同一条管道，保证行为一致）。</summary>
    private static async Task<int> RunImportKbAsync(AppConfig config, string path)
    {
        var store = new KnowledgeStore(
            Path.Combine(AppConfigStore.GetConfigDirectory(), "data", "knowledge.db"),
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

    private static int RunExport(Database db, string formatArg, string outputPath)
    {
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

    private static void PrintProviders(AppConfig config)
    {
        Console.WriteLine($"Provider（{config.Providers.Count}）：");
        foreach (var p in config.Providers)
            Console.WriteLine($"  [{(p.Enabled ? "启用" : "停用")}] {p.Name} → {p.Endpoint} / {p.ChatModel}");
        Console.WriteLine("分流规则：");
        foreach (var r in config.Rules)
            Console.WriteLine($"  {r.TaskType}: {(r.ProviderChain.Count > 0 ? string.Join(" → ", r.ProviderChain) : "(未配置)")}");
    }

    private static async Task<int> RunChatAsync(AppConfig config, string text)
    {
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
}
