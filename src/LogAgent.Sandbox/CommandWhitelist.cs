using System.Text.RegularExpressions;
using LogAgent.Shared.Configuration;

namespace LogAgent.Sandbox;

/// <summary>命令白名单/黑名单校验（规划 3.9：白名单优先 + 黑名单正则兜底）。</summary>
public sealed class CommandWhitelist(SandboxConfig config)
{
    private readonly List<Regex> _blacklist = [..
        config.BlacklistPatterns.Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Compiled))];

    private readonly HashSet<string> _whitelist = [..
        config.WhitelistCommands.Select(w => w.ToLowerInvariant().Trim())];

    /// <summary>校验命令是否允许执行。返回 (允许, 拒绝原因)。</summary>
    public (bool Allowed, string? Reason) Check(string commandLine)
    {
        var trimmed = commandLine.Trim();
        if (trimmed.Length == 0) return (false, "空命令");

        foreach (var pattern in _blacklist)
            if (pattern.IsMatch(trimmed))
                return (false, $"命中黑名单规则：{pattern}");

        var executable = ExtractExecutable(trimmed);
        // Windows 下 cmd 内建命令按白名单处理；带路径的取文件名
        var name = Path.GetFileName(executable.Trim('"')).ToLowerInvariant();
        if (!_whitelist.Contains(name))
            return (false, $"可执行文件 {name} 不在白名单");
        return (true, null);
    }

    private static string ExtractExecutable(string commandLine)
    {
        if (commandLine.StartsWith('"'))
        {
            var end = commandLine.IndexOf('"', 1);
            return end > 0 ? commandLine[..(end + 1)] : commandLine;
        }
        var space = commandLine.IndexOf(' ');
        return space < 0 ? commandLine : commandLine[..space];
    }
}
