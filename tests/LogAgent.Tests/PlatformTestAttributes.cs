using Xunit;

namespace LogAgent.Tests;

// 平台条件测试属性：xUnit 2.9 没有运行时 Skip 判定，用 FactAttribute 的 Skip 属性在构造期按平台设置。
// 用法与 [Fact]/[Theory] 相同，只是「不在目标平台时自动跳过」。

/// <summary>仅 Windows 运行（Linux CI 上跳过）。</summary>
public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "仅 Windows 平台";
    }
}

/// <summary>仅 Windows 运行（Linux CI 上跳过）。</summary>
public sealed class WindowsOnlyTheoryAttribute : TheoryAttribute
{
    public WindowsOnlyTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "仅 Windows 平台";
    }
}

/// <summary>仅非 Windows 运行（Windows 开发机上跳过）。</summary>
public sealed class UnixOnlyFactAttribute : FactAttribute
{
    public UnixOnlyFactAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "仅 Linux / macOS 平台";
    }
}

/// <summary>仅非 Windows 运行（Windows 开发机上跳过）。</summary>
public sealed class UnixOnlyTheoryAttribute : TheoryAttribute
{
    public UnixOnlyTheoryAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "仅 Linux / macOS 平台";
    }
}
