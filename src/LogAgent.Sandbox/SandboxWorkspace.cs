namespace LogAgent.Sandbox;

/// <summary>每任务临时工作区（规划 3.9）。Shell 执行/Job Object 隔离在 M3 落地。</summary>
public sealed class SandboxWorkspace : IDisposable
{
    public string Root { get; }
    public string TaskId { get; }

    public SandboxWorkspace(string sandboxBase, string? taskId = null)
    {
        TaskId = taskId ?? Guid.NewGuid().ToString("N")[..12];
        Root = Path.Combine(sandboxBase, TaskId);
        Directory.CreateDirectory(Root);
    }

    /// <summary>沙箱内列文件（自动过滤越界项，返回相对路径）。</summary>
    public IReadOnlyList<string> ListFiles(string relativeDir = ".")
    {
        var dir = SandboxPathGuard.EnsureInside(Root, relativeDir);
        if (!Directory.Exists(dir)) return [];
        return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Where(f => SandboxPathGuard.IsInside(Root, f))
            .Select(f => Path.GetRelativePath(Root, f))
            .ToList();
    }

    public string Resolve(string relativePath) => SandboxPathGuard.EnsureInside(Root, relativePath);

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { /* 文件被占用，留待清理任务 */ }
    }
}
