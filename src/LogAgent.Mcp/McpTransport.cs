using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using LogAgent.Shared.Configuration;
using LogAgent.Shared.Platform;

namespace LogAgent.Mcp;

/// <summary>
/// MCP 传输抽象：面向"行"的消息交换（每行一条 JSON-RPC 消息）。
/// 实现：StdioTransport（本地子进程）/ HttpTransport（Streamable HTTP）。
/// </summary>
public interface IMcpTransport : IAsyncDisposable
{
    /// <summary>建立传输（stdio：拉起子进程）。</summary>
    Task StartAsync(CancellationToken ct);

    /// <summary>发送一行 JSON 消息。</summary>
    Task WriteLineAsync(string json, CancellationToken ct);

    /// <summary>接收一行 JSON 消息（null = 对端关闭）。</summary>
    Task<string?> ReadLineAsync(CancellationToken ct);

    /// <summary>对端是否存活。</summary>
    bool IsAlive { get; }

    /// <summary>人类可读的传输描述（日志用）。</summary>
    string Describe();
}

/// <summary>
/// stdio 传输：启动本地 MCP 服务器子进程，stdin/stdout 交换换行分隔的 JSON-RPC 消息。
/// Windows 下自动解析 PATH 中的 .exe/.cmd/.bat（npx 等）并用 cmd 包裹批处理入口。
/// </summary>
public sealed class StdioTransport(McpServerEntry config) : IMcpTransport
{
    private Process? _process;

    /// <summary>服务器 stderr 输出（诊断信息，MCP 规范允许；转发为日志）。</summary>
    public event EventHandler<string>? StderrLine;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public bool IsAlive => _process is { HasExited: false };

    public string Describe() => $"stdio: {config.Command} {config.Args}".TrimEnd();

    public Task StartAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.Command))
            throw new InvalidOperationException("stdio 传输缺少命令（Command）");

        var (fileName, arguments) = ResolveLaunch(config.Command, config.Args);
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
        };
        if (!string.IsNullOrWhiteSpace(config.WorkingDirectory) && Directory.Exists(config.WorkingDirectory))
            psi.WorkingDirectory = config.WorkingDirectory;
        foreach (var (key, value) in config.Env)
            psi.Environment[key] = value;

        _process = Process.Start(psi) ?? throw new InvalidOperationException($"无法启动进程：{fileName}");
        _process.StandardInput.AutoFlush = true;

        // stderr 后台读取（服务器诊断输出不占用协议通道）
        var proc = _process;
        _ = Task.Run(async () =>
        {
            try
            {
                while (await proc.StandardError.ReadLineAsync() is { } line)
                    if (line.Trim().Length > 0) StderrLine?.Invoke(this, line);
            }
            catch (Exception)
            {
                // 进程退出时流关闭，忽略
            }
        });
        return Task.CompletedTask;
    }

    public async Task WriteLineAsync(string json, CancellationToken ct)
    {
        if (_process is null || _process.HasExited)
            throw new InvalidOperationException("服务器进程未运行");
        await _process.StandardInput.WriteLineAsync(json.AsMemory(), ct);
    }

    public async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        if (_process is null) return null;
        try
        {
            return await _process.StandardOutput.ReadLineAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null; // 进程结束/流关闭
        }
    }

    public async ValueTask DisposeAsync()
    {
        var proc = _process;
        _process = null;
        if (proc is null) return;

        try { proc.StandardInput.Close(); } catch (Exception) { /* 已关闭 */ }
        try
        {
            using var cts = new CancellationTokenSource(3000);
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch (Exception) { /* 已退出 */ }
        }
        proc.Dispose();
    }

    // ─────────────────────────── 命令解析 ───────────────────────────

    /// <summary>
    /// 解析可执行入口：Windows 下在 PATH 中找 .exe/.cmd/.bat；
    /// 命中 .cmd/.bat（如 npx.cmd）时用 cmd.exe 包裹（CreateProcess 不识别批处理）。
    /// Unix 下直接执行，但会校验脚本的可执行位（缺少时给出 chmod +x 提示）。
    /// </summary>
    internal static (string FileName, string Arguments) ResolveLaunch(string command, string args)
    {
        var cmd = command.Trim();
        var arguments = args.Trim();
        if (PlatformInfo.IsUnix)
        {
            if (cmd.Contains('/') && File.Exists(cmd) && !HasExecutePermission(cmd))
                throw new InvalidOperationException($"命令缺少执行权限：{cmd}（执行 chmod +x \"{cmd}\" 后重试）");
            return (cmd, arguments);
        }

        var exe = FindExecutable(cmd);
        if (exe is null) return (cmd, arguments);

        if (exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            || exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            // cmd /d /s /c "…" —— /s 语义：整个字符串作为命令；路径带空格时用内层引号包裹
            var inner = arguments.Length == 0 ? $"\"{exe}\"" : $"\"{exe}\" {arguments}";
            return ("cmd.exe", $"/d /s /c \"{inner}\"");
        }
        return (exe, arguments);
    }

    /// <summary>Unix 下判断文件是否带执行位（Windows / 非 Unix 文件系统视为可执行）。</summary>
    internal static bool HasExecutePermission(string path)
    {
        if (!PlatformInfo.IsUnix) return true;
        try
        {
            var mode = File.GetUnixFileMode(path);
            return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return true; // 无法读取权限位时不阻断（交由真实启动失败给出错误）
        }
    }

    /// <summary>在 PATH 中查找命令（Windows 优先 .exe，其次 .cmd/.bat，最后无扩展名；Unix 只按原名）。</summary>
    internal static string? FindExecutable(string command)
    {
        if (command.Contains('\\') || command.Contains('/'))
            return File.Exists(command) ? Path.GetFullPath(command) : null;

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        // Windows 优先 .exe/.cmd/.bat；Unix 只按原名查找（扩展名无执行语义）
        var extensions = PlatformInfo.IsUnix ? new[] { "" } : new[] { ".exe", ".cmd", ".bat", "" };
        foreach (var ext in extensions)
        {
            foreach (var dir in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                try
                {
                    var candidate = Path.Combine(dir.Trim(), command + ext);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException)
                {
                    // PATH 中的非法字符路径，跳过
                }
            }
        }
        return null;
    }
}

/// <summary>
/// Streamable HTTP 传输（MCP 2025 规范）：每行请求 POST 到服务器 URL，
/// 响应为 application/json 或 text/event-stream（SSE）；会话 id 自动携带。
/// </summary>
public sealed class HttpTransport(McpServerEntry config) : IMcpTransport
{
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly Channel<string> _incoming = Channel.CreateUnbounded<string>();
    private string? _sessionId;

    public bool IsAlive => true;

    public string Describe() => $"http: {config.Url}";

    public Task StartAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.Url) || !config.Url.Contains("://", StringComparison.Ordinal))
            throw new InvalidOperationException("http 传输缺少合法 URL（Url）");
        return Task.CompletedTask;
    }

    public async Task WriteLineAsync(string json, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, config.Url);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (_sessionId is not null)
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
        foreach (var (key, value) in config.Headers)
            request.Headers.TryAddWithoutValidation(key, value);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(300));
        using var resp = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

        if (resp.Headers.TryGetValues("Mcp-Session-Id", out var ids))
            _sessionId = ids.FirstOrDefault();

        // 通知/已接收：202/204 无响应体
        if (resp.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.NoContent)
            return;
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(cts.Token);
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode}：{Truncate(body, 300)}");
        }

        var contentType = resp.Content.Headers.ContentType?.MediaType ?? "";
        if (contentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            await foreach (var data in ReadSseDataAsync(resp, cts.Token))
                await _incoming.Writer.WriteAsync(data, cts.Token);
        }
        else
        {
            var body = (await resp.Content.ReadAsStringAsync(cts.Token)).Trim();
            if (body.Length > 0)
                await _incoming.Writer.WriteAsync(body, cts.Token);
        }
    }

    public async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        try
        {
            return await _incoming.Reader.ReadAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    public ValueTask DisposeAsync()
    {
        _incoming.Writer.TryComplete();
        _http.Dispose();
        return ValueTask.CompletedTask;
    }

    private static async IAsyncEnumerable<string> ReadSseDataAsync(
        HttpResponseMessage resp, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim();
            if (data.Length > 0 && data != "[DONE]")
                yield return data;
        }
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
