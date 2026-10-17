using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LogAgent.Shared.Configuration;

namespace LogAgent.NapCat;

/// <summary>
/// NapCat 进程托管：以「启动脚本」为入口（用户直接选择 napcat.bat / launcher.bat / .exe），
/// 工作目录取脚本所在目录；支持 webui.json 自动读取 Token；进程 stdout/stderr 全量接入日志（日志绑定）。
/// Windows 桌面形态托管本地 NapCat（Shell 版）；Linux 无头形态建议直接用 NapCat Docker。
/// </summary>
public sealed class NapCatManager
{
    // webui.json 的字段是全小写（token/port），必须大小写不敏感反序列化
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private Process? _process;
    private Task? _outputPump;

    public NapCatConfig Config { get; }

    public NapCatManager(NapCatConfig config) => Config = config;

    /// <summary>启动脚本已配置且文件存在。</summary>
    public bool IsInstalled =>
        !string.IsNullOrWhiteSpace(Config.ScriptPath) && File.Exists(Config.ScriptPath);

    /// <summary>进程在运行（含本实例托管之外、此前遗留运行的 NapCat）。</summary>
    public bool IsRunning => (_process is not null && !_process.HasExited) || ExternalNapCatExists();

    /// <summary>系统里存在 NapCat 引导进程（避免应用重启后重复拉起）。</summary>
    private static bool ExternalNapCatExists()
        => Process.GetProcessesByName("NapCatWinBootMain").Length > 0;

    public string? WebUiToken
    {
        get
        {
            if (!string.IsNullOrEmpty(Config.WebUiToken)) return Config.WebUiToken;
            var webuiJson = Path.Combine(Config.ResolveDirectory(), "config", "webui.json");
            if (!File.Exists(webuiJson)) return null;
            try
            {
                var parsed = JsonSerializer.Deserialize<NapCatWebUiFile>(File.ReadAllText(webuiJson), JsonOpts);
                return string.IsNullOrEmpty(parsed?.Token) ? null : parsed.Token;
            }
            catch (JsonException) { return null; }
        }
    }

    public event EventHandler<string>? ManagerLog;

    /// <summary>把「反向 WS 连接"补入"到 NapCat 配置（模板 + 所有已登录账号），返回更新的文件数。</summary>
    public int InjectReverseWsConfig(int port, string token)
    {
        var count = NapCatConfigWriter.EnsureReverseWs(Config.ResolveDirectory(), port, token).Count;
        ManagerLog?.Invoke(this, count > 0
            ? $"已补入反向 WS 配置（{count} 个配置文件，地址 ws://127.0.0.1:{port}/onebot/v11/ws）"
            : "未找到可写入的 NapCat 配置文件（确认目录结构：config/onebot11.json）");
        return count;
    }

    /// <summary>按启动脚本拉起 NapCat（.bat/.cmd 经 cmd 执行；stdout/stderr 实时转发到日志）。</summary>
    public Task StartAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("本机进程托管仅支持 Windows；Linux 请使用 NapCat Docker + 远程 WebUI 对接");
        if (string.IsNullOrWhiteSpace(Config.ScriptPath))
            throw new InvalidOperationException("未配置 NapCat 启动脚本（请在「OneBot 连接」页选择）");
        if (!File.Exists(Config.ScriptPath))
            throw new InvalidOperationException($"启动脚本不存在：{Config.ScriptPath}");
        if (IsRunning)
        {
            ManagerLog?.Invoke(this, "检测到 NapCat 已在运行（复用以避免双开）");
            return Task.CompletedTask;
        }

        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{Config.ScriptPath}\"",
            WorkingDirectory = Config.ResolveDirectory(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        _process = Process.Start(psi) ?? throw new InvalidOperationException("NapCat 启动失败");
        ManagerLog?.Invoke(this, $"NapCat 已启动（脚本 {Path.GetFileName(Config.ScriptPath)}，PID {_process.Id}）");

        // 日志绑定：持续读取进程输出并转发（进程退出后泵自动结束）
        _outputPump = Task.Run(() => PumpOutputAsync(_process, ct), CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task PumpOutputAsync(Process process, CancellationToken ct)
    {
        await Task.WhenAll(
            PumpStreamAsync(process.StandardOutput, "out", ct),
            PumpStreamAsync(process.StandardError, "err", ct));
    }

    private async Task PumpStreamAsync(StreamReader reader, string tag, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null) break;
                if (line.Trim().Length > 0)
                    ManagerLog?.Invoke(this, $"[{tag}] {line}");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or IOException)
        {
            // 进程已退出/流已关闭
        }
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        if (_process is { HasExited: false } p)
        {
            try { p.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* 已退出 */ }
        }
        _process = null;
        _outputPump = null;

        // 兜底清理：NapCat 引导进程可能已脱离 cmd 进程树（bat 内 start），按其专属特征清理
        KillIf("NapCatWinBootMain", _ => true, "NapCat 引导进程");
        KillIf("QQ", cmd => cmd.Contains("--enable-logging", StringComparison.OrdinalIgnoreCase),
            "NapCat 注入的 QQ 主进程");
        KillIf("cmd", cmd => cmd.Contains("launcher.bat", StringComparison.OrdinalIgnoreCase),
            "NapCat 启动器 cmd");

        ManagerLog?.Invoke(this, "NapCat 已停止");
        return Task.CompletedTask;
    }

    /// <summary>按「进程名 + 命令行特征」精确清理进程（不触碰用户日常 QQ 等无关进程）。</summary>
    private void KillIf(string processName, Func<string, bool> predicate, string label)
    {
        foreach (var proc in Process.GetProcessesByName(processName))
        {
            try
            {
                if (!predicate(GetCommandLine(proc.Id))) continue;
                proc.Kill();
                ManagerLog?.Invoke(this, $"[清理] 已结束{label}（PID {proc.Id}）");
            }
            catch (Exception) { /* 已退出或无权限 */ }
            finally { proc.Dispose(); }
        }
    }

    private static string GetCommandLine(int pid)
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {pid}");
            foreach (var obj in searcher.Get())
                return obj["CommandLine"]?.ToString() ?? "";
        }
        catch (Exception) { }
        return "";
    }
}
