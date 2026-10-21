using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using LogAgent.Connections;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QRCoder;

// 独立小网页：手机 QQ 扫码 → 获取 QQ 官方 Bot 的 AppID / AppSecret。
// 不读写 LogAgent 配置，不进入主程序；仅监听 127.0.0.1。
var port = ParsePort(args);
if (port is < 1 or > 65535)
{
    Console.Error.WriteLine("端口必须在 1-65535 之间");
    return 1;
}

var (listen, listenText) = ParseListen(args);
var verbose = args.Contains("--verbose") || args.Contains("-v");
var builder = WebApplication.CreateSlimBuilder();
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(o =>
{
    if (listen.Equals(IPAddress.Any) || listen.Equals(IPAddress.IPv6Any))
        o.ListenAnyIP(port);
    else
        o.Listen(listen, port);
});
await using var app = builder.Build();

var sessions = new SessionStore();
app.Lifetime.ApplicationStopping.Register(sessions.CancelAll);

app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["Cache-Control"] = "no-store";
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    if (!verbose)
    {
        await next();
        return;
    }

    var started = Stopwatch.GetTimestamp();
    await next();
    var elapsed = Stopwatch.GetElapsedTime(started);
    Console.WriteLine($"{DateTime.Now:HH:mm:ss} {ctx.Request.Method} {ctx.Request.Path}{ctx.Request.QueryString} " +
                      $"→ {ctx.Response.StatusCode} ({elapsed.TotalMilliseconds:F0}ms)");
});

app.MapGet("/", () => Results.Content(PageHtml.Value, "text/html; charset=utf-8"));
app.MapGet("/api/health", () => Results.Json(new { ok = true, time = DateTimeOffset.Now }));

app.MapPost("/api/start", async () =>
{
    var session = await sessions.StartAsync();
    return Results.Json(new { id = session.Id });
});

app.MapGet("/api/status/{id}", (string id) =>
{
    var snapshot = sessions.GetSnapshot(id);
    return snapshot is null
        ? Results.NotFound()
        : Results.Json(new
        {
            status = snapshot.Status,
            appId = snapshot.AppId,
            appSecret = snapshot.AppSecret,
            error = snapshot.Error,
        });
});

app.MapGet("/api/qr/{id}", (string id) =>
{
    var png = sessions.GetQrPng(id);
    return png is null ? Results.NotFound() : Results.File(png, "image/png");
});

try
{
    await app.StartAsync();
}
catch (Exception ex)
{
    var hint = ex.InnerException is SocketException socket
        ? socket.SocketErrorCode switch
        {
            SocketError.AccessDenied => "端口被系统保留或没有权限，请换一个 1024 以上的端口（--port 6711）。",
            SocketError.AddressAlreadyInUse => "端口已被占用，请换一个端口（--port 6711）。",
            _ => null,
        }
        : ex.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
            ? "端口已被占用，请换一个端口（--port 6711）。"
            : null;
    Console.Error.WriteLine($"启动失败：{ex.Message}");
    if (hint is not null) Console.Error.WriteLine($"提示：{hint}");
    return 1;
}

var loopbackUrl = $"http://127.0.0.1:{port}/";
if (listen.Equals(IPAddress.Loopback))
{
    Console.WriteLine($"QQ 官方 Bot 扫码页：{loopbackUrl}");
    Console.WriteLine("只监听本机回环地址；按 Ctrl+C 退出。");
    Console.WriteLine("如果手机/别的电脑要访问，用 --lan（或 --listen 0.0.0.0）并放行防火墙端口。");
}
else
{
    Console.WriteLine("QQ 官方 Bot 扫码页（已监听所有网卡）：");
    Console.WriteLine($"  本机：{loopbackUrl}");
    foreach (var url in LanUrls(port)) Console.WriteLine($"  局域网：{url}");
    Console.WriteLine($"  监听：{listenText}:{port}");
    Console.WriteLine("⚠️ 页面会显示 AppSecret：只应在可信局域网使用，用完立即 Ctrl+C 关闭。");
    Console.WriteLine("Windows 首次监听时如弹防火墙，请允许专用网络；Linux 请放行对应端口。");
}

if (IsTermux())
{
    Console.WriteLine("检测到 Termux：切到浏览器前先执行 termux-wake-lock，");
    Console.WriteLine("并在系统设置里关闭 Termux 的电池优化；否则 Android 可能冻结进程，导致轮询 Failed to fetch。");
}
Console.WriteLine(verbose ? "详细请求日志：已开启（--verbose）" : "排查连接问题可加 --verbose 查看请求日志。");

try { await app.WaitForShutdownAsync(); }
catch (OperationCanceledException) { }
return 0;

static (IPAddress Address, string Text) ParseListen(string[] args)
{
    var text = "127.0.0.1";
    var allowLan = false;
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i] is "--lan" or "--allow-lan")
        {
            allowLan = true;
            continue;
        }
        if (args[i] is "--listen" or "--host" && i + 1 < args.Length)
        {
            text = args[++i];
            continue;
        }
        if (args[i].StartsWith("--listen=", StringComparison.Ordinal))
        {
            text = args[i]["--listen=".Length..];
            continue;
        }
    }

    if (allowLan && text == "127.0.0.1") text = "0.0.0.0";
    var normalized = text.Trim().ToLowerInvariant();
    return normalized switch
    {
        "0.0.0.0" or "any" or "*" or "all" => (IPAddress.Any, "0.0.0.0"),
        "::" or "[::]" => (IPAddress.IPv6Any, "::"),
        "localhost" => (IPAddress.Loopback, "127.0.0.1"),
        _ when IPAddress.TryParse(text, out var parsed) => (parsed, parsed.ToString()),
        _ => (IPAddress.Loopback, "127.0.0.1"),
    };
}

static IEnumerable<string> LanUrls(int port)
{
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
    {
        if (ni.OperationalStatus != OperationalStatus.Up ||
            ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            continue;
        foreach (var address in ni.GetIPProperties().UnicastAddresses)
        {
            var ip = address.Address;
            if (ip.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ip))
                continue;
            if (seen.Add(ip.ToString()))
                yield return $"http://{ip}:{port}/";
        }
    }
}

static bool IsTermux()
{
    static bool HasTermux(string? value) =>
        value?.Contains("com.termux", StringComparison.OrdinalIgnoreCase) == true;

    return HasTermux(Environment.GetEnvironmentVariable("PREFIX")) ||
           HasTermux(Environment.GetEnvironmentVariable("HOME")) ||
           HasTermux(AppContext.BaseDirectory) ||
           Directory.Exists("/data/data/com.termux");
}

static int ParsePort(string[] args)
{
    var port = 6710;
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i] is "--port" or "-p" && i + 1 < args.Length &&
            int.TryParse(args[i + 1], out var next))
        {
            port = next;
            i++;
        }
        else if (args[i].StartsWith("--port=", StringComparison.Ordinal) &&
                 int.TryParse(args[i]["--port=".Length..], out var inline))
        {
            port = inline;
        }
    }
    return port;
}

internal static class PageHtml
{
    public static readonly string Value = Load();

    private static string Load()
    {
        var assembly = typeof(PageHtml).Assembly;
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("index.html", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("缺少嵌入资源 index.html");
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"无法读取 {name}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}

internal sealed record QrSnapshot(string Status, string? AppId, string? AppSecret, string? Error);

/// <summary>一次扫码会话：创建绑定任务 → 轮询 → 过期自动换码 → 拿到凭证。</summary>
internal sealed class QrSession
{
    private readonly object _gate = new();
    private readonly QrConnectClient _client = new();
    private readonly CancellationTokenSource _cts = new();
    private byte[]? _qrPng;
    private string _status = "pending";
    private string? _appId;
    private string? _appSecret;
    private string? _error;

    public string Id { get; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;

    public async Task StartAsync()
    {
        try
        {
            await BeginNewQrAsync();
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
            return;
        }
        _ = Task.Run(PollLoopAsync);
    }

    private async Task BeginNewQrAsync()
    {
        var url = await _client.StartAsync("LogAgent-QRLogin", _cts.Token);
        var png = RenderQr(url);
        lock (_gate)
        {
            _qrPng = png;
            _status = "pending";
            _error = null;
            _appId = null;
            _appSecret = null;
        }
    }

    private async Task PollLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), _cts.Token);
                var result = await _client.PollAsync(_cts.Token);
                if (result.Status == QrBindStatus.Completed)
                {
                    if (string.IsNullOrWhiteSpace(result.AppSecret))
                    {
                        SetError("平台未返回 AppSecret，请重新扫码");
                        return;
                    }
                    lock (_gate)
                    {
                        _status = "completed";
                        _appId = result.AppId;
                        _appSecret = result.AppSecret;
                        _error = null;
                    }
                    return;
                }

                if (result.Status == QrBindStatus.Expired)
                {
                    lock (_gate) _status = "expired";
                    await BeginNewQrAsync();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetError(ex.Message); }
    }

    public QrSnapshot Snapshot()
    {
        lock (_gate) return new(_status, _appId, _appSecret, _error);
    }

    public byte[]? QrPng()
    {
        lock (_gate) return _qrPng;
    }

    public void Cancel()
    {
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    private void SetError(string error)
    {
        lock (_gate)
        {
            _status = "error";
            _error = error;
        }
    }

    private static byte[] RenderQr(string url)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        return new PngByteQRCode(data).GetGraphic(8);
    }
}

internal sealed class SessionStore
{
    private readonly ConcurrentDictionary<string, QrSession> _sessions = new();

    public async Task<QrSession> StartAsync()
    {
        Cleanup();
        var session = new QrSession();
        _sessions[session.Id] = session;
        await session.StartAsync();
        return session;
    }

    public QrSnapshot? GetSnapshot(string id) =>
        _sessions.TryGetValue(id, out var session) ? session.Snapshot() : null;

    public byte[]? GetQrPng(string id) =>
        _sessions.TryGetValue(id, out var session) ? session.QrPng() : null;

    public void CancelAll()
    {
        foreach (var (id, session) in _sessions.ToArray())
        {
            session.Cancel();
            _sessions.TryRemove(id, out _);
        }
    }

    private void Cleanup()
    {
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-30);
        foreach (var (id, session) in _sessions.ToArray())
        {
            if (session.CreatedAt >= cutoff) continue;
            session.Cancel();
            _sessions.TryRemove(id, out _);
        }
    }
}
