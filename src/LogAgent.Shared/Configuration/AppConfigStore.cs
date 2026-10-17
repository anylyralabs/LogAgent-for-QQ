using System.Text.Json;
using System.Text.Json.Serialization;

namespace LogAgent.Shared.Configuration;

/// <summary>配置文件存取。路径跨平台：Windows %AppData%/LogAgent，Linux ~/.config/LogAgent。</summary>
public static class AppConfigStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string GetConfigDirectory()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(baseDir)) baseDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(baseDir, "LogAgent");
    }

    public static string GetConfigPath() => Path.Combine(GetConfigDirectory(), "config.json");

    public static AppConfig Load()
    {
        var path = GetConfigPath();
        if (!File.Exists(path)) return AppConfig.CreateDefault();
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var config = JsonSerializer.Deserialize<AppConfig>(doc.RootElement.GetRawText(), JsonOpts);
        return config ?? AppConfig.CreateDefault();
    }

    public static void Save(AppConfig config)
    {
        var dir = GetConfigDirectory();
        Directory.CreateDirectory(dir);
        var path = GetConfigPath();
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(config, JsonOpts));
        File.Move(tmp, path, overwrite: true);
    }
}
