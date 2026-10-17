using System.Text.Json;
using System.Text.Json.Serialization;

namespace LogAgent.Skills;

/// <summary>
/// 技能清单（skill.json，或 SKILL.md 的 frontmatter）：
/// 一个技能 = 一组可被模型调用的 HTTP 工具（可空）+ 可选的指令正文（SKILL.md，渐进式披露）+ 用户可配置项。
/// 约定：工具参数用 {param} 占位、技能设置用 {setting.key} 占位，在执行时替换。
/// </summary>
public sealed class SkillManifest
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Author { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>技能类型："http"（工具型）/ "prompt"（指令型，SKILL.md）。</summary>
    public string Type { get; set; } = "http";

    /// <summary>指令正文（SKILL.md markdown；模型通过 read_skill 按需读取）。</summary>
    public string? Instructions { get; set; }

    /// <summary>接口基础地址（工具 Path 拼接在其后）。</summary>
    public string BaseUrl { get; set; } = "";

    public List<SkillSettingSpec> Settings { get; set; } = [];
    public List<SkillToolSpec> Tools { get; set; } = [];

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>解析清单（无效时返回 null：须有 Id）。工具列表可以为空（纯指令技能）。</summary>
    public static SkillManifest? Parse(string json)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<SkillManifest>(json, Options);
            return manifest is { Id.Length: > 0 } ? manifest : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>从 skill.json 文件加载（失败返回 null）。</summary>
    public static SkillManifest? Load(string skillJsonPath)
    {
        try
        {
            return Parse(File.ReadAllText(skillJsonPath));
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// 解析 SKILL.md（Claude/WorkBuddy 生态标准格式）：
    /// 可选 YAML frontmatter（--- name: … description: … version: … ---）+ markdown 指令正文。
    /// </summary>
    public static SkillManifest? ParseSkillMd(string markdown, string fallbackId)
    {
        var (meta, body) = SplitFrontmatter(markdown);
        var name = meta.GetValueOrDefault("name") ?? "";
        // id 解析顺序：frontmatter "id" → frontmatter "name"（Claude 技能格式里 name 即标识）→ 目录名
        var rawId = meta.GetValueOrDefault("id");
        if (string.IsNullOrWhiteSpace(rawId)) rawId = name;
        var id = Slug(string.IsNullOrWhiteSpace(rawId) ? fallbackId : rawId, fallbackId);
        if (id.Length == 0) return null;
        var instructions = body.Trim();
        var description = meta.GetValueOrDefault("description") ?? "";
        if (instructions.Length == 0 && description.Length == 0) return null; // 无正文也无描述：视为半成品，跳过

        return new SkillManifest
        {
            Id = id,
            Name = name.Length > 0 ? name : id,
            Version = meta.GetValueOrDefault("version") ?? "",
            Author = meta.GetValueOrDefault("author") ?? "",
            Description = description.Length > 0 ? description : FirstLine(instructions),
            Type = "prompt",
            Instructions = instructions,
        };
    }

    /// <summary>提取 SKILL.md 的正文部分（去掉 frontmatter；用于 http 型技能的补充说明）。</summary>
    public static string ExtractMarkdownBody(string markdown) => SplitFrontmatter(markdown).Body.Trim();

    /// <summary>拆分 frontmatter（简单 key: value 行；不支持的嵌套语法忽略）。</summary>
    private static (Dictionary<string, string> Meta, string Body) SplitFrontmatter(string markdown)
    {
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var text = markdown.Replace("\r\n", "\n");
        if (!text.StartsWith("---\n", StringComparison.Ordinal))
            return (meta, text);

        var end = text.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0) return (meta, text);

        var header = text[4..end];
        foreach (var line in header.Split('\n'))
        {
            var idx = line.IndexOf(':');
            if (idx <= 0) continue;
            var key = line[..idx].Trim();
            var value = line[(idx + 1)..].Trim().Trim('"', '\'');
            if (key.Length > 0 && value.Length > 0)
                meta[key] = value;
        }
        return (meta, text[(end + 4)..]);
    }

    /// <summary>把技能名规整为 id（小写字母数字与连字符）。</summary>
    private static string Slug(string raw, string fallback)
    {
        var source = string.IsNullOrWhiteSpace(raw) ? fallback : raw;
        var sb = new System.Text.StringBuilder();
        foreach (var c in source.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c)) sb.Append(c);
            else if (c is '-' or '_' or ' ' or '.') sb.Append('-');
        }
        var slug = sb.ToString().Trim('-');
        return slug.Length > 0 ? slug : "skill";
    }

    private static string FirstLine(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var t = line.Trim().TrimStart('#', ' ').Trim();
            if (t.Length > 0) return t.Length > 120 ? t[..120] : t;
        }
        return "";
    }
}

/// <summary>技能设置项（用户在技能页配置；占位符 {setting.key}）。</summary>
public sealed class SkillSettingSpec
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Default { get; set; } = "";
}

/// <summary>工具定义：一次 HTTP 调用模板。</summary>
public sealed class SkillToolSpec
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Method { get; set; } = "GET";

    /// <summary>相对路径（可含 {param} / {setting.key} 占位符）。</summary>
    public string Path { get; set; } = "";

    public Dictionary<string, string> Headers { get; set; } = new();

    /// <summary>POST 请求体模板（JSON；字符串值中可含占位符）。</summary>
    public JsonElement? BodyTemplate { get; set; }

    public List<SkillParamSpec> Params { get; set; } = [];
}

/// <summary>工具参数定义（用于生成模型可见的 JSON Schema）。</summary>
public sealed class SkillParamSpec
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>"string"（默认）或 "integer"。</summary>
    public string Type { get; set; } = "string";

    [JsonPropertyName("required")]
    public bool Required { get; set; }
}
