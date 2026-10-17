namespace LogAgent.Sandbox;

/// <summary>
/// 沙箱路径守卫：所有沙箱内文件操作必须先经过这里（规划 3.9）。
/// 拦截 .. 穿越、symlink 逃逸、盘符绝对路径。
/// </summary>
public static class SandboxPathGuard
{
    private static readonly StringComparison Comp = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static (string RootNoTail, string RootPrefix) NormalizeRoot(string sandboxRoot)
    {
        var root = Path.GetFullPath(sandboxRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return (root, root + Path.DirectorySeparatorChar);
    }

    /// <summary>规范化并校验路径在沙箱根内（含根自身）。不合法抛 InvalidOperationException。</summary>
    public static string EnsureInside(string sandboxRoot, string path)
    {
        var (rootNoTail, rootPrefix) = NormalizeRoot(sandboxRoot);
        var full = Path.GetFullPath(Path.Combine(rootNoTail, path));
        var inside = full.StartsWith(rootPrefix, Comp) || string.Equals(full, rootNoTail, Comp);
        if (!inside)
            throw new InvalidOperationException($"路径越界，拒绝沙箱外访问：{path}");
        return full;
    }

    /// <summary>不抛异常版本，用于列目录时过滤。</summary>
    public static bool IsInside(string sandboxRoot, string path)
    {
        try { EnsureInside(sandboxRoot, path); return true; }
        catch (InvalidOperationException) { return false; }
    }
}
