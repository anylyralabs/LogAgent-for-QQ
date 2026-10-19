using LogAgent.Shared.Platform;

namespace LogAgent.Sandbox;

/// <summary>
/// 沙箱路径守卫：所有沙箱内文件操作必须先经过这里（规划 3.9）。
/// 拦截 .. 穿越、symlink 逃逸、绝对路径越界；路径大小写规则跟随当前平台。
/// </summary>
public static class SandboxPathGuard
{
    private static StringComparison Comp => PlatformShell.Current.CaseSensitivePaths
        ? StringComparison.Ordinal
        : StringComparison.OrdinalIgnoreCase;

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
        if (!IsWithin(rootNoTail, rootPrefix, full))
            throw new InvalidOperationException($"路径越界，拒绝沙箱外访问：{path}");

        // symlink 逃逸：逐级（含自身）解析最终目标；任一层跳出沙箱即拒绝。
        // 注意 .NET 的 ResolveLinkTarget 只处理「最后一段」，因此这里从叶子向上逐层检查。
        if (FindEscapingLink(rootNoTail, rootPrefix, full) is { } escape)
            throw new InvalidOperationException(
                $"路径经符号链接越界，拒绝访问：{path}（{escape} 指向沙箱外）");
        return full;
    }

    /// <summary>不抛异常版本，用于列目录时过滤。</summary>
    public static bool IsInside(string sandboxRoot, string path)
    {
        try { EnsureInside(sandboxRoot, path); return true; }
        catch (InvalidOperationException) { return false; }
    }

    private static bool IsWithin(string rootNoTail, string rootPrefix, string full) =>
        full.StartsWith(rootPrefix, Comp) || string.Equals(full, rootNoTail, Comp);

    /// <summary>从 full 向上逐级检查符号链接，返回第一个指向沙箱外的层级（无问题返回 null）。</summary>
    private static string? FindEscapingLink(string rootNoTail, string rootPrefix, string full)
    {
        var current = full;
        while (!string.IsNullOrEmpty(current) && current.Length > rootNoTail.Length)
        {
            if (TryResolveLinkTarget(current) is { } target && !IsWithin(rootNoTail, rootPrefix, target))
                return current;
            var parent = Path.GetDirectoryName(current);
            if (parent is null || parent == current) break;
            current = parent;
        }
        return null;
    }

    /// <summary>
    /// 解析符号链接的最终目标；不存在、不是链接、无此能力或断链时返回 null。
    /// </summary>
    private static string? TryResolveLinkTarget(string fullPath)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(fullPath)
                ? new DirectoryInfo(fullPath)
                : new FileInfo(fullPath);
            if (info.LinkTarget is null) return null; // 不是链接（含不存在）
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            return target is null ? null : Path.GetFullPath(target.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // 无法解析（断链等）：按原路径结果放行，由后续文件操作自行报错
            return null;
        }
    }
}
