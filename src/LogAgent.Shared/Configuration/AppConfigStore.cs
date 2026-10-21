using System.Text.Json;
using System.Text.Json.Serialization;
using LogAgent.Shared.Platform;

namespace LogAgent.Shared.Configuration;

/// <summary>
/// 配置文件存取。路径跨平台：Windows %AppData%/LogAgent，Linux/macOS ~/.config/LogAgent。
/// 可用环境变量 LOGAGENT_CONFIG_DIR 覆盖（容器/多实例/无家目录的服务账号用）。
/// </summary>
public static class AppConfigStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>数据目录（config.json / data / files / sandbox / skills 的根）。</summary>
    public static string GetConfigDirectory()
    {
        var overrideDir = Environment.GetEnvironmentVariable("LOGAGENT_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDir)) return Path.GetFullPath(overrideDir.Trim());

        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(baseDir)) baseDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(baseDir, "LogAgent");
    }

    /// <summary>SQLite 与会话数据目录。</summary>
    public static string GetDataDirectory() => Path.Combine(GetConfigDirectory(), "data");

    /// <summary>QQ 文件与抓取日志的暂存目录。</summary>
    public static string GetFilesDirectory() => Path.Combine(GetConfigDirectory(), "files");

    /// <summary>沙箱任务工作区根目录。</summary>
    public static string GetSandboxDirectory() => Path.Combine(GetConfigDirectory(), "sandbox");

    /// <summary>用户技能目录（内置技能在程序目录的 skills/ 下）。</summary>
    public static string GetSkillsDirectory() => Path.Combine(GetConfigDirectory(), "skills");

    public static string GetConfigPath() => Path.Combine(GetConfigDirectory(), "config.json");

    public static AppConfig Load()
    {
        var path = GetConfigPath();
        if (!File.Exists(path)) return AppConfig.CreateDefault();
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var config = JsonSerializer.Deserialize<AppConfig>(doc.RootElement.GetRawText(), JsonOpts);
        if (config is null) return AppConfig.CreateDefault();
        // 旧配置（只有 OneBot11 / OfficialBot 并列字段）在这里迁成频道模型；
        // 幂等，放在唯一读写入口最省心。
        config.NormalizeChannels();
        return config;
    }

    public static void Save(AppConfig config)
    {
        // 保存前也规整一次：调用方可能直接 new AppConfig 或只改旧字段；
        // 保证落盘时 Channels 已存在且格式完整（幂等）。
        config.NormalizeChannels();
        var dir = GetConfigDirectory();
        Directory.CreateDirectory(dir);
        var path = GetConfigPath();
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(config, JsonOpts));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>备份当前配置（返回备份路径；配置不存在时返回 null）。</summary>
    public static string? Backup()
    {
        var path = GetConfigPath();
        if (!File.Exists(path)) return null;
        var backup = $"{path}.bak-{DateTime.Now:yyyyMMdd-HHmmss}";
        File.Copy(path, backup, overwrite: true);
        return backup;
    }

    /// <summary>平台摘要（doctor 与启动日志用）。</summary>
    public static string DescribePlatform() =>
        $"{PlatformInfo.Describe()} · {PlatformShell.Current.DisplayName} · {GetConfigDirectory()}";
}
