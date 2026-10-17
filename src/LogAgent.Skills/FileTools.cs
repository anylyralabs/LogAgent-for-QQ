using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LogAgent.Sandbox;
using LogAgent.Shared.Chat;

namespace LogAgent.Skills;

/// <summary>
/// 内置文件工具（模型驱动，全部限定在 QQ 文件暂存目录内）：
/// · read_file  —— 按行区间读取文件的任意部分（offset/limit，带行号）；
/// · search_file —— 搜索关键词（正则/原文自动回退），返回命中行号与上下文；
/// · run_shell  —— 沙箱执行 Windows cmd 命令（白名单+黑名单+超时+审计），用于 findstr/type/python 等进一步处理。
/// 大日志完整落盘后，由模型自己决定「看哪一段、搜什么、跑什么命令」——不再由代码截取头尾省略中间。
/// </summary>
public sealed class FileTools(string filesDir, SandboxRunner? sandboxRunner = null)
{
    public const string ReadFileTool = "read_file";
    public const string SearchFileTool = "search_file";
    public const string RunShellTool = "run_shell";

    private const int MaxReadChars = 16000;    // read_file 输出上限（字符）
    private const int MaxSearchChars = 16000;  // search_file 输出上限
    private const int MaxShellChars = 12000;   // run_shell 输出上限
    private const int MaxLineChars = 800;      // 单行截断（防超长行污染上下文）

    public event EventHandler<string>? ToolLog;

    public static bool IsBuiltinTool(string name) =>
        name is ReadFileTool or SearchFileTool or RunShellTool;

    /// <summary>构建工具定义（shell 仅在沙箱启用且执行器可用时暴露）。</summary>
    public IReadOnlyList<ChatToolDefinition> BuildToolDefinitions(bool shellEnabled)
    {
        var tools = new List<ChatToolDefinition>
        {
            new(ReadFileTool,
                "读取本机已保存文件（QQ 收到的文件 / 抓取的日志）的指定行区间，带行号返回——文件位于 files 目录。分析大日志时用它精读任意区段（中间内容都在，自己去取）。参数：path（文件名或完整路径）；offset（起始行号，从 1 开始，默认 1；读结尾可以先看返回的总行数再取 offset）；limit（读取行数，默认 300，最大 800）。",
                """{"type":"object","properties":{"path":{"type":"string","description":"文件名或完整路径（须在 files 目录内）"},"offset":{"type":"integer","description":"起始行号（从 1 开始），默认 1"},"limit":{"type":"integer","description":"读取行数，默认 300，最大 800"}},"required":["path"]}"""),
            new(SearchFileTool,
                "在已保存文件里搜索关键词（默认按正则匹配，含特殊字符时自动按原文处理；忽略大小写），返回命中行的行号、内容与上下文。大日志分析先用它定位错误区段（如搜 Exception、Caused by、SIGSEGV、Mod），再用 read_file 精读命中附近内容。参数：path；pattern（要搜的词或正则）；context（每处命中显示前后各几行，0-5，默认 2）；max_matches（最多返回几处，默认 40，最大 100）。",
                """{"type":"object","properties":{"path":{"type":"string","description":"文件名或完整路径（须在 files 目录内）"},"pattern":{"type":"string","description":"要搜索的关键词或正则表达式"},"context":{"type":"integer","description":"命中行前后各显示的上下文行数，0-5，默认 2"},"max_matches":{"type":"integer","description":"最多返回的命中数量，默认 40，最大 100"}},"required":["path","pattern"]}"""),
        };
        if (shellEnabled && sandboxRunner is not null)
        {
            tools.Add(new ChatToolDefinition(RunShellTool,
                "在 files 目录执行 Windows cmd 命令做日志分析（工作目录即文件保存目录，用相对文件名即可）。可执行文件受沙箱白名单限制：findstr、find、type、more、sort、dir、python 等；示例：findstr /n /i \"Exception\" 文件名.log；more +100 文件名.log（从第 100 行看起）。超时约 30 秒、输出超限自动截断。参数：command。",
                """{"type":"object","properties":{"command":{"type":"string","description":"要执行的 cmd 命令（工作目录为 files 目录）"}},"required":["command"]}"""));
        }
        return tools;
    }

    public async Task<string> ExecuteAsync(string toolName, string argumentsJson, CancellationToken ct = default)
    {
        JsonObject args;
        try
        {
            args = JsonNode.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson) as JsonObject
                   ?? new JsonObject();
        }
        catch (JsonException)
        {
            return $"错误：{toolName} 参数不是合法 JSON";
        }

        return toolName switch
        {
            ReadFileTool => ReadFile(args),
            SearchFileTool => SearchFile(args),
            RunShellTool => await RunShellAsync(args, ct),
            _ => $"错误：未知内置工具 {toolName}",
        };
    }

    /// <summary>清理暂存目录里的过期文件（启动时调用，尽力而为）。</summary>
    public static int CleanupOldFiles(string dir, TimeSpan maxAge)
    {
        if (!Directory.Exists(dir)) return 0;
        var cutoff = DateTime.UtcNow - maxAge;
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                    removed++;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return removed;
    }

    // ---------- read_file ----------

    private string ReadFile(JsonObject args)
    {
        var path = ResolvePath(args, out var error);
        if (path is null) return $"错误：{error}";
        var offset = Math.Max(1, GetInt(args, "offset", 1));
        var limit = Math.Clamp(GetInt(args, "limit", 300), 1, 800);
        var name = Path.GetFileName(path);

        var sb = new StringBuilder();
        int total = 0, shown = 0, lastShown = offset - 1;
        var clipped = false;
        foreach (var line in ReadLines(path))
        {
            total++;
            if (total < offset || shown >= limit) continue;
            if (sb.Length >= MaxReadChars)
            {
                clipped = true;
                continue; // 已达输出上限：继续只数总行数
            }
            sb.Append(total).Append(" | ").Append(ClipLine(line)).Append('\n');
            shown++;
            lastShown = total;
        }

        if (total == 0) return $"文件「{name}」是空文件（0 行）。";
        if (offset > total)
            return $"文件：{name} | 共 {total} 行，但请求的起始行 {offset} 超出末尾。读结尾可以试 offset={Math.Max(1, total - 100)}。";

        var header = $"文件：{name} | 共 {total} 行 | 显示 {offset}-{lastShown} 行";
        var note = clipped ? $"\n……（输出已达上限，读到第 {lastShown} 行；继续读取用 offset={lastShown + 1}）" : "";
        EmitLog($"read_file {name}：第 {offset}-{lastShown} 行（共 {total} 行）");
        return header + "\n" + sb.ToString().TrimEnd('\n') + note;
    }

    // ---------- search_file ----------

    private string SearchFile(JsonObject args)
    {
        var path = ResolvePath(args, out var error);
        if (path is null) return $"错误：{error}";
        var pattern = GetString(args, "pattern").Trim();
        if (pattern.Length == 0) return "错误：缺少必填参数 pattern";
        var context = Math.Clamp(GetInt(args, "context", 2), 0, 5);
        var maxMatches = Math.Clamp(GetInt(args, "max_matches", 40), 1, 100);
        var name = Path.GetFileName(path);

        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
        }
        catch (RegexParseException)
        {
            regex = new Regex(Regex.Escape(pattern), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
        }

        var sb = new StringBuilder();
        int scanned = 0, matches = 0, lastPrinted = 0, after = 0;
        var truncated = false;
        var before = new LinkedList<(int No, string Line)>();
        try
        {
            foreach (var line in ReadLines(path))
            {
                scanned++;
                if (regex.IsMatch(line))
                {
                    if (matches >= maxMatches)
                    {
                        truncated = true;
                        break;
                    }
                    matches++;
                    var firstToPrint = before.First is { } f && f.Value.No > lastPrinted ? f.Value.No : scanned;
                    if (lastPrinted > 0 && firstToPrint > lastPrinted + 1) sb.Append("--\n");
                    foreach (var (no, text) in before)
                    {
                        if (no <= lastPrinted) continue;
                        sb.Append(no).Append(":   ").Append(ClipLine(text)).Append('\n');
                        lastPrinted = no;
                    }
                    before.Clear();
                    sb.Append(scanned).Append(": > ").Append(ClipLine(line)).Append('\n');
                    lastPrinted = scanned;
                    after = context;
                }
                else if (after > 0)
                {
                    sb.Append(scanned).Append(":   ").Append(ClipLine(line)).Append('\n');
                    lastPrinted = scanned;
                    after--;
                }
                else
                {
                    before.AddLast((scanned, line));
                    while (before.Count > context) before.RemoveFirst();
                }

                if (sb.Length > MaxSearchChars)
                {
                    truncated = true;
                    break;
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return $"错误：搜索「{pattern}」时正则匹配超时（模式过于复杂），请改用更简单的关键词分段搜索。";
        }

        var header = $"文件：{name} | 已扫描 {scanned} 行 | 命中 {matches} 处" +
                     (truncated ? "（已截断，可能还有更多，可缩小关键词或加大 max_matches）" : "");
        EmitLog($"search_file 「{pattern}」← {name}：命中 {matches} 处");
        if (matches == 0)
            return header + $"\n未命中。建议：换更短的关键词（如只搜 \"Exception\" 或 \"error\"），或先用 read_file 查看文件开头/结尾了解日志结构。";
        return header + "\n" + sb.ToString().TrimEnd('\n');
    }

    // ---------- run_shell ----------

    private async Task<string> RunShellAsync(JsonObject args, CancellationToken ct)
    {
        if (sandboxRunner is null)
            return "错误：shell 工具未启用（沙箱已关闭；可在配置文件的 Sandbox.Enabled 打开）";
        var command = GetString(args, "command").Trim();
        if (command.Length == 0) return "错误：缺少必填参数 command";

        EmitLog($"run_shell：{command}");
        var result = await sandboxRunner.RunAsync(filesDir, command, ct);
        var output = result.Output.Length > MaxShellChars
            ? result.Output[..MaxShellChars] + "\n……（输出超限已截断）"
            : result.Output;
        if (string.IsNullOrWhiteSpace(output)) output = "(无输出)";
        var status = result.TimedOut ? "超时被杀" : $"exit {result.ExitCode}";
        return $"$ {command}\n[{status} · {result.Duration.TotalSeconds:F1}s]\n{output}";
    }

    // ---------- 公共辅助 ----------

    private string? ResolvePath(JsonObject args, out string error)
    {
        error = "";
        var raw = GetString(args, "path").Trim().Trim('"');
        if (raw.Length == 0)
        {
            error = "缺少必填参数 path";
            return null;
        }
        string full;
        try
        {
            full = Path.GetFullPath(Path.IsPathRooted(raw) ? raw : Path.Combine(filesDir, raw));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            error = $"路径不合法：{raw}";
            return null;
        }
        var root = Path.GetFullPath(filesDir);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            error = $"只能访问 files 目录内的文件（{root}）。QQ 收到的文件与抓取的日志都保存在这里。";
            return null;
        }
        if (Directory.Exists(full))
        {
            error = $"「{Path.GetFileName(full)}」是目录不是文件";
            return null;
        }
        if (!File.Exists(full))
        {
            error = $"文件不存在：{full}。可用 run_shell 执行 dir 查看目录里的文件。";
            return null;
        }
        return full;
    }

    private static IEnumerable<string> ReadLines(string path)
    {
        using var reader = new StreamReader(path, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        while (reader.ReadLine() is { } line)
            yield return line;
    }

    private static string ClipLine(string line) =>
        line.Length > MaxLineChars ? line[..MaxLineChars] + "…" : line;

    private static int GetInt(JsonObject args, string key, int fallback)
    {
        var node = args[key];
        if (node is null) return fallback;
        try
        {
            return node.GetValueKind() switch
            {
                JsonValueKind.Number => node.GetValue<int>(),
                JsonValueKind.String => int.TryParse(node.GetValue<string>(), out var v) ? v : fallback,
                _ => fallback,
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or OverflowException)
        {
            return fallback; // 含超大数值（超出 int）等异常输入
        }
    }

    private static string GetString(JsonObject args, string key)
    {
        var node = args[key];
        if (node is null) return "";
        try
        {
            return node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : node.ToJsonString();
        }
        catch (InvalidOperationException)
        {
            return "";
        }
    }

    private void EmitLog(string line) => ToolLog?.Invoke(this, line);
}
