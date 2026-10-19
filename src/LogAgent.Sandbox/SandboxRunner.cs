using System.Diagnostics;
using System.Text;
using LogAgent.Shared.Configuration;
using LogAgent.Shared.Platform;

namespace LogAgent.Sandbox;

/// <summary>命令审计条目（UI 沙箱管理页数据源）。</summary>
public sealed record CommandAuditEntry(
    DateTimeOffset Ts, string TaskId, string Command, bool Allowed,
    int ExitCode, bool TimedOut, TimeSpan Duration, string? RejectReason, int OutputBytes);

/// <summary>
/// 沙箱 Shell 执行器：cwd 锁定工作区、白名单校验、超时杀进程树、输出截断、全量审计。
/// 具体 shell 由 <see cref="PlatformShell"/> 决定（Windows cmd / POSIX sh）。
/// 进程资源硬限制（Job Object / cgroups）为 IProcessIsolator 进阶项，M3 以超时+杀树实现。
/// </summary>
public sealed class SandboxRunner(SandboxConfig config)
{
    private static int _taskSeq;

    public event EventHandler<CommandAuditEntry>? Audited;

    public string NextTaskId() => $"task-{Interlocked.Increment(ref _taskSeq)}";

    public async Task<CommandResult> RunAsync(string workingDirectory, string command, CancellationToken ct = default)
    {
        var taskId = NextTaskId();
        var whitelist = new CommandWhitelist(config);
        var (allowed, reason) = whitelist.Check(command);
        if (!allowed)
        {
            var rejected = new CommandResult(-1, $"[拒绝] {reason}", TimeSpan.Zero, true);
            Audit(new CommandAuditEntry(DateTimeOffset.Now, taskId, command, false, -1, false, TimeSpan.Zero, reason, 0));
            return rejected;
        }

        var spec = PlatformShell.Current.BuildLaunch(command);
        var psi = new ProcessStartInfo
        {
            FileName = spec.FileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        if (spec.ArgumentList is { Count: > 0 })
        {
            foreach (var arg in spec.ArgumentList) psi.ArgumentList.Add(arg);
        }
        else
        {
            psi.Arguments = spec.Arguments ?? "";
        }
        foreach (var (key, value) in spec.Environment) psi.Environment[key] = value;

        var started = Stopwatch.StartNew();
        try
        {
            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("进程启动失败");
            var timeout = TimeSpan.FromSeconds(Math.Max(5, config.CommandTimeoutSeconds));
            var readTask = Task.WhenAll(process.StandardOutput.ReadToEndAsync(ct), process.StandardError.ReadToEndAsync(ct));

            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                process.Kill(entireProcessTree: true);
                var partial = await readTask;
                var result = new CommandResult(-1, Truncate(partial[0] + partial[1]) + "\n[超时，进程树已被终止]", started.Elapsed, true);
                Audit(MakeEntry(taskId, command, result));
                return result;
            }

            var outputs = await readTask;
            var output = Truncate(outputs[0] + (outputs[1].Length > 0 ? "\n[stderr]\n" + outputs[1] : ""));
            var ok = new CommandResult(process.ExitCode, output, started.Elapsed, false);
            Audit(MakeEntry(taskId, command, ok));
            return ok;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // 命令不存在 / 无执行权限：给出可操作的提示，而不是原始系统错误
            var failed = new CommandResult(-1, $"[无法执行] {spec.FileName}：{ex.Message}（检查该命令是否已安装、是否有执行权限）", started.Elapsed, false);
            Audit(MakeEntry(taskId, command, failed));
            return failed;
        }
        catch (Exception ex)
        {
            var failed = new CommandResult(-1, $"[执行异常] {ex.Message}", started.Elapsed, false);
            Audit(MakeEntry(taskId, command, failed));
            return failed;
        }
    }

    private CommandAuditEntry MakeEntry(string taskId, string command, CommandResult r) =>
        new(DateTimeOffset.Now, taskId, command, true, r.ExitCode, r.TimedOut, r.Duration, null,
            Encoding.UTF8.GetByteCount(r.Output));

    private string Truncate(string output)
    {
        var maxBytes = config.MaxOutputKb * 1024;
        var bytes = Encoding.UTF8.GetBytes(output);
        if (bytes.Length <= maxBytes) return output;
        return Encoding.UTF8.GetString(bytes, 0, maxBytes) + "\n[输出超限，已截断]";
    }

    private void Audit(CommandAuditEntry entry) => Audited?.Invoke(this, entry);
}

/// <summary>命令执行结果。</summary>
public sealed record CommandResult(int ExitCode, string Output, TimeSpan Duration, bool TimedOut);
