using System.IO.Compression;
using LogAgent.Shared.Configuration;

namespace LogAgent.Sandbox;

/// <summary>
/// 沙箱门面：统一管理任务工作区、文件操作、下载、解压、Shell 执行与审计（规划 3.9）。
/// 所有路径经 SandboxPathGuard 校验，拒绝越界。
/// </summary>
public sealed class SandboxService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };

    public SandboxService(SandboxConfig config, string sandboxBaseDir)
    {
        Config = config;
        BaseDir = sandboxBaseDir;
        Runner = new SandboxRunner(config);
        // 审计接线放构造函数：Runner 被直接使用（如文件工具 run_shell）时审计同样生效
        Runner.Audited += OnAudit;
    }

    public SandboxConfig Config { get; }
    public string BaseDir { get; }
    public SandboxRunner Runner { get; }

    /// <summary>审计日志（内存环形，UI 展示）。</summary>
    private readonly Queue<CommandAuditEntry> _audit = new();

    public event EventHandler<CommandAuditEntry>? Audited;

    public IReadOnlyList<CommandAuditEntry> RecentAudit(int count = 50)
    {
        lock (_audit) return _audit.TakeLast(count).ToList();
    }

    public SandboxWorkspace CreateWorkspace(string? taskId = null)
        => new(BaseDir, taskId ?? Runner.NextTaskId());

    private void OnAudit(object? sender, CommandAuditEntry e)
    {
        lock (_audit)
        {
            _audit.Enqueue(e);
            while (_audit.Count > 500) _audit.Dequeue();
        }
        Audited?.Invoke(this, e);
    }

    // ---------- 文件操作（全部限定工作区内） ----------

    public void WriteFile(SandboxWorkspace ws, string relativePath, string content)
    {
        var path = ws.Resolve(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public string ReadFile(SandboxWorkspace ws, string relativePath)
        => File.ReadAllText(ws.Resolve(relativePath));

    public void DeleteFile(SandboxWorkspace ws, string relativePath)
        => File.Delete(ws.Resolve(relativePath));

    /// <summary>解压 zip 到工作区子目录。</summary>
    public void ExtractZip(SandboxWorkspace ws, string zipPath, string destDir = "extracted")
    {
        var src = ws.Resolve(zipPath);
        var dest = ws.Resolve(destDir);
        ZipFile.ExtractToDirectory(src, dest, overwriteFiles: true);
    }

    /// <summary>下载 URL 到工作区（仅 http/https，大小/类型限制）。</summary>
    public async Task<string> DownloadAsync(SandboxWorkspace ws, string url, string? saveAs = null, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
            throw new InvalidOperationException("仅允许 http/https 下载");

        var fileName = saveAs ?? Path.GetFileName(uri.AbsolutePath);
        if (string.IsNullOrEmpty(fileName) || !SandboxPathGuard.IsInside(ws.Root, fileName))
            fileName = "download.bin";

        var destPath = ws.Resolve(fileName);
        using var resp = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var declared = resp.Content.Headers.ContentLength ?? 0;
        var maxBytes = (long)Config.MaxDownloadMb * 1024 * 1024;
        if (declared > maxBytes)
            throw new InvalidOperationException($"文件大小 {declared / 1024 / 1024}MB 超过上限 {Config.MaxDownloadMb}MB");

        await using var httpStream = await resp.Content.ReadAsStreamAsync(ct);
        await using var fileStream = File.Create(destPath);
        var copied = 0L;
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await httpStream.ReadAsync(buffer, ct)) > 0)
        {
            copied += read;
            if (copied > maxBytes) throw new InvalidOperationException("下载超过大小上限，已中止");
            await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return Path.GetRelativePath(ws.Root, destPath);
    }
}
