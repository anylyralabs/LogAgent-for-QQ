using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LogAgent.Shared.Configuration;
using LogAgent.Shared.Platform;

namespace LogAgent.NapCat;

/// <summary>
/// NapCat 进程托管：以「启动脚本」为入口（Windows：napcat.bat / launcher.bat / .exe；
/// Linux：napcat.sh / start.sh 等可执行脚本），工作目录取脚本所在目录；
/// 支持 webui.json 自动读取 Token；进程 stdout/stderr 全量接入日志（日志绑定）。
/// Windows 桌面形态托管本地 NapCat（Shell 版）；Linux 可托管 Shell 脚本，也可直接用 NapCat Docker。
/// </summary>
public sealed class NapCatManager
{
    // webui.json 的字段是全小写（token/port），必须大小写不敏感反序列化
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private Process? _process;
    private Task? _outputPump;

    public NapCatConfig Config { get; }

    public NapCatManager(NapCatConfig config) => Config = config;

    /// <summary>本机是否运行 Windows（决定启动方式与进程探测实现）。</summary>
    public static bool IsWindowsHost => PlatformInfo.Current == OsFamily.Windows;

    /// <summary>启动脚本已配置且文件存在。</summary>
    public bool IsInstalled =>
        !string.IsNullOrWhiteSpace(Config.ScriptPath) && File.Exists(Config.ScriptPath);

    /// <summary>进程在运行（含本实例托管之外、此前遗留运行的 NapCat）。</summary>
    public bool IsRunning => (_process is not null && !_process.HasExited) || ExternalNapCatExists();

    /// <summary>系统里存在 NapCat 引导进程（避免应用重启后重复拉起）。</summary>
    private static bool ExternalNapCatExists()
    {
        if (IsWindowsHost)
            return Process.GetProcessesByName("NapCatWinBootMain").Length > 0;

        // Linux：NapCat 桌面版（LinuxQQ + NapCat）主进程名通常是 napcat / NapCat；
        // 只做「是否存在」判定，不主动杀进程（StopAsync 里再按特征清理）。
        foreach (var proc in Process.GetProcesses())
        {
            var name = NameOf(proc);
            if (name is "napcat" or "NapCat" or "NapCatWinBootMain") return true;
        }
        return false;
    }

    /// <summary>取进程名（拿不到时返回空串；调用即释放句柄）。</summary>
    private static string NameOf(Process p)
    {
        try { return p.ProcessName; }
        catch (Exception) { return ""; }
        finally { p.Dispose(); }
    }

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

    /// <summary>把「反向 WS 连接」补入到 NapCat 配置（模板 + 所有已登录账号），返回更新的文件数。</summary>
    public int InjectReverseWsConfig(int port, string token)
    {
        var count = NapCatConfigWriter.EnsureReverseWs(Config.ResolveDirectory(), port, token).Count;
        ManagerLog?.Invoke(this, count > 0
            ? $"已补入反向 WS 配置（{count} 个配置文件，地址 ws://127.0.0.1:{port}/onebot/v11/ws）"
            : "未找到可写入的 NapCat 配置文件（确认目录结构：config/onebot11.json）");
        return count;
    }

    /// <summary>按启动脚本拉起 NapCat（Windows 的 .bat/.cmd 经 cmd 执行；Unix 脚本直接执行）。</summary>
    public Task StartAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(Config.ScriptPath))
            throw new InvalidOperationException("未配置 NapCat 启动脚本（请在「OneBot 连接」页选择）");
        if (!File.Exists(Config.ScriptPath))
            throw new InvalidOperationException($"启动脚本不存在：{Config.ScriptPath}");
        if (IsRunning)
        {
            ManagerLog?.Invoke(this, "检测到 NapCat 已在运行（复用以避免双开）");
            return Task.CompletedTask;
        }

        var psi = BuildStartInfo(Config.ScriptPath);
        _process = Process.Start(psi) ?? throw new InvalidOperationException("NapCat 启动失败");
        ManagerLog?.Invoke(this, $"NapCat 已启动（脚本 {Path.GetFileName(Config.ScriptPath)}，PID {_process.Id}）");

        // 日志绑定：持续读取进程输出并转发（进程退出后泵自动结束）
        _outputPump = Task.Run(() => PumpOutputAsync(_process, ct), CancellationToken.None);
        return Task.CompletedTask;
    }

    private ProcessStartInfo BuildStartInfo(string scriptPath)
    {
        var psi = new ProcessStartInfo
        {
            WorkingDirectory = Config.ResolveDirectory(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };

        if (IsWindowsHost)
        {
            psi.FileName = "cmd.exe";
            psi.Arguments = $"/c \"{scriptPath}\"";
            return psi;
        }

        var ext = Path.GetExtension(scriptPath).ToLowerInvariant();
        switch (ext)
        {
            case ".sh":
                psi.FileName = File.Exists("/bin/sh") ? "/bin/sh" : "/bin/bash";
                psi.ArgumentList.Add(scriptPath);
                break;
            case ".exe":
                // Windows 可执行文件在 Linux 上只在 mono/wine 下有意义，直接执行会得到明确的 ENOEXEC
                psi.FileName = scriptPath;
                break;
            default:
                // 无扩展名或其它脚本：直接执行（依赖 shebang 与可执行位）
                if (!HasExecutePermission(scriptPath))
                    throw new InvalidOperationException(
                        $"启动脚本缺少执行权限：{scriptPath}（执行 chmod +x \"{scriptPath}\" 后重试）");
                psi.FileName = scriptPath;
                break;
        }
        return psi;
    }

    private static bool HasExecutePermission(string path)
    {
        // 用 OperatingSystem.IsWindows() 让分析器做平台流分析（File.GetUnixFileMode 在 Windows 上不可用）
        if (OperatingSystem.IsWindows()) return true;
        try
        {
            var mode = File.GetUnixFileMode(path);
            return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return true;
        }
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

        // 兜底清理：NapCat 引导进程可能已脱离启动器进程树（bat/sh 内 start &），按其专属特征清理。
        // 只在 Windows 上按进程名清理：Linux 上同名的 QQ 进程属于用户的其它登录实例，
        // 误杀代价高，交由用户的 systemd/docker 生命周期管理。
        if (IsWindowsHost)
        {
            KillIf("NapCatWinBootMain", _ => true, "NapCat 引导进程");
            KillIf("QQ", cmd => cmd.Contains("--enable-logging", StringComparison.OrdinalIgnoreCase),
                "NapCat 注入的 QQ 主进程");
            KillIf("cmd", cmd => cmd.Contains("launcher.bat", StringComparison.OrdinalIgnoreCase),
                "NapCat 启动器 cmd");
        }
        else
        {
            // Unix：只清理我们自己拉起的进程树（上面已 Kill(entireProcessTree)），外加 NapCat 主进程名精确匹配
            KillIf("napcat", _ => true, "NapCat 进程");
            KillIf("NapCat", _ => true, "NapCat 进程");
        }

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

    /// <summary>读取进程命令行：Windows 走 WMI，Unix 读 /proc/{pid}/cmdline（权限不足时返回空）。</summary>
    private static string GetCommandLine(int pid)
    {
        if (!IsWindowsHost)
        {
            try
            {
                var path = $"/proc/{pid}/cmdline";
                if (!File.Exists(path)) return "";
                // /proc/*/cmdline 用 NUL 分隔参数
                return File.ReadAllText(path).Replace('\0', ' ');
            }
            catch (Exception)
            {
                return "";
            }
        }

        // Windows 实现：System.Management 是跨平台包，非 Windows 调用会抛 PlatformNotSupportedException，
        // 因此用运行时守卫（上面的提前返回）而不是条件编译——项目 TFM 保持 net10.0，双平台同一份代码。
#pragma warning disable CA1416 // 只在 Windows 分支执行（运行时已守卫）
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {pid}");
            foreach (var obj in searcher.Get())
                return obj["CommandLine"]?.ToString() ?? "";
        }
        catch (Exception) { }
#pragma warning restore CA1416
        return "";
    }
}
