using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace LogAgent.Skills;

/// <summary>商店目录条目（catalog.json 中的一条技能）。</summary>
public sealed record CatalogEntry(
    string Id, string Name, string Version, string Description, string Author, string DownloadUrl, string Homepage);

/// <summary>技能商店目录：解析 / 拉取 catalog.json（{"skills":[...]}）。</summary>
public static class SkillCatalog
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>解析目录 JSON（容错：坏条目跳过）。</summary>
    public static List<CatalogEntry> Parse(string json)
    {
        var entries = new List<CatalogEntry>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("skills", out var skills) || skills.ValueKind != JsonValueKind.Array)
            return entries;
        foreach (var item in skills.EnumerateArray())
        {
            try
            {
                var entry = JsonSerializer.Deserialize<CatalogEntry>(item.GetRawText(), Options);
                if (entry is { Id.Length: > 0 }) entries.Add(entry);
            }
            catch (JsonException)
            {
                // 跳过坏条目
            }
        }
        return entries;
    }

    /// <summary>拉取并解析商店目录。</summary>
    public static async Task<List<CatalogEntry>> FetchAsync(string url, CancellationToken ct = default)
        => Parse(await Http.GetStringAsync(url, ct));
}

/// <summary>技能安装器：zip（本地文件或 URL）、目录拷贝；安装到用户技能目录。</summary>
public sealed class SkillInstaller(string userSkillsDir)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };

    /// <summary>从 zip（本地路径或 http(s) URL）安装；返回结果描述。同名技能目录会被替换。</summary>
    public async Task<string> InstallFromZipAsync(string pathOrUrl, CancellationToken ct = default)
    {
        string zipPath;
        var tempDir = Path.Combine(Path.GetTempPath(), $"la-skill-{Guid.NewGuid():N}");
        try
        {
            if (pathOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(tempDir);
                zipPath = Path.Combine(tempDir, "skill.zip");
                var bytes = await Http.GetByteArrayAsync(pathOrUrl, ct);
                await File.WriteAllBytesAsync(zipPath, bytes, ct);
            }
            else
            {
                zipPath = pathOrUrl;
                if (!File.Exists(zipPath)) return $"文件不存在：{zipPath}";
            }

            var extractDir = Path.Combine(tempDir, "extract");
            Directory.CreateDirectory(extractDir);
            ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);
            return InstallFromFolder(FindSkillRoot(extractDir) ?? extractDir);
        }
        catch (InvalidDataException ex)
        {
            return $"不是有效的 zip 包：{ex.Message}";
        }
        catch (Exception ex)
        {
            return $"安装失败：{ex.Message}";
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>从目录安装（目录内须有 skill.json；支持包根目录多一层的情况由调用方处理）。</summary>
    public string InstallFromFolder(string srcDir)
    {
        var manifest = SkillManifest.Load(Path.Combine(srcDir, "skill.json"));
        if (manifest is null) return "安装失败：目录中没有有效的 skill.json（须含 id 与至少一个工具）";
        if (!IsSafeId(manifest.Id)) return $"安装失败：技能 id 不合法（{manifest.Id}）";

        Directory.CreateDirectory(userSkillsDir);
        var target = Path.Combine(userSkillsDir, manifest.Id);
        try
        {
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            CopyDirectory(srcDir, target);
            return $"已安装技能「{manifest.Name}」（{manifest.Id} v{manifest.Version}）";
        }
        catch (IOException ex)
        {
            return $"安装失败：{ex.Message}";
        }
    }

    /// <summary>卸载用户技能（内置技能不可卸载）；返回结果描述。</summary>
    public string Uninstall(string skillId)
    {
        if (!IsSafeId(skillId)) return $"卸载失败：技能 id 不合法（{skillId}）";
        var target = Path.Combine(userSkillsDir, skillId);
        if (!Directory.Exists(target) || !File.Exists(Path.Combine(target, "skill.json")))
            return $"卸载失败：未找到用户技能 {skillId}（内置技能不可卸载）";
        try
        {
            Directory.Delete(target, recursive: true);
            return $"已卸载技能 {skillId}";
        }
        catch (IOException ex)
        {
            return $"卸载失败：{ex.Message}";
        }
    }

    /// <summary>在解压目录中定位 skill.json 所在根（兼容 zip 内多套一层目录）。</summary>
    private static string? FindSkillRoot(string dir)
    {
        if (File.Exists(Path.Combine(dir, "skill.json"))) return dir;
        foreach (var sub in Directory.EnumerateDirectories(dir))
            if (File.Exists(Path.Combine(sub, "skill.json")))
                return sub;
        return null;
    }

    private static bool IsSafeId(string id) =>
        id.Length is > 0 and <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var dest = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }
}
