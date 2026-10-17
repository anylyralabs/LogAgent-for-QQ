using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using LogAgent.Shared.Chat;
using LogAgent.Shared.Configuration;

namespace LogAgent.Skills;

/// <summary>技能工具宿主：向 Agent 暴露工具定义并执行调用（由 Agent 的工具循环驱动）。</summary>
public interface ISkillToolHost
{
    /// <summary>构建当前启用技能的工具定义（模型可见）。</summary>
    IReadOnlyList<ChatToolDefinition> BuildToolDefinitions(SkillsConfig config);

    /// <summary>指令型技能（SKILL.md）的提示块（注入系统提示；无则返回空串）。</summary>
    string BuildPromptSkillsBlock(SkillsConfig config);

    /// <summary>执行一次工具调用，返回给模型的文本结果（失败返回"错误：…"文本而非抛异常）。</summary>
    Task<string> ExecuteToolAsync(string toolName, string argumentsJson, SkillsConfig config, CancellationToken ct = default);
}

/// <summary>
/// 技能注册表：扫描「内置技能目录 + 用户技能目录」（各含多个技能子目录）。
/// 技能两种形态：skill.json（HTTP 工具型，占位符替换 + HTTP 调用，SSE 自动聚合）
/// 与 SKILL.md（指令型，Claude/WorkBuddy 生态标准格式，经 read_skill 渐进式披露）。
/// </summary>
public sealed class SkillRegistry(string builtinDir, string userDir) : ISkillToolHost
{
    public sealed record LoadedSkill(SkillManifest Manifest, string Directory, bool Builtin);

    private readonly List<LoadedSkill> _skills = [];

    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>序列化选项：中文原文输出（避免 \uXXXX 转义）。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private const int MaxResultChars = 8000;
    private const int MaxSkillDocChars = 12000;

    public event EventHandler<string>? SkillLog;

    public IReadOnlyList<LoadedSkill> Skills => _skills;

    public string BuiltinDir => builtinDir;
    public string UserDir => userDir;

    /// <summary>（重新）扫描两个目录加载技能。</summary>
    public void Reload()
    {
        _skills.Clear();
        var builtinCount = LoadDir(builtinDir, builtin: true);
        var userCount = LoadDir(userDir, builtin: false);
        Log($"已加载 {_skills.Count} 个技能（内置 {builtinCount} / 用户 {userCount}）");
    }

    private int LoadDir(string dir, bool builtin)
    {
        if (!Directory.Exists(dir)) return 0;
        var loaded = 0;
        foreach (var sub in Directory.EnumerateDirectories(dir).OrderBy(d => d, StringComparer.Ordinal))
        {
            var manifest = LoadSkillDir(sub);
            if (manifest is null)
            {
                Log($"技能目录无效，已跳过：{sub}");
                continue;
            }
            _skills.Add(new LoadedSkill(manifest, sub, builtin));
            loaded++;
        }
        return loaded;
    }

    /// <summary>
    /// 加载单个技能目录：优先 skill.json（HTTP 工具型；如附带 SKILL.md 则作为补充说明），
    /// 否则找 SKILL.md（指令型，frontmatter + markdown 正文）。
    /// </summary>
    public static SkillManifest? LoadSkillDir(string dir)
    {
        var manifestPath = Path.Combine(dir, "skill.json");
        var skillMdPath = Path.Combine(dir, "SKILL.md");
        try
        {
            if (File.Exists(manifestPath))
            {
                var manifest = SkillManifest.Load(manifestPath);
                if (manifest is not null && File.Exists(skillMdPath))
                    manifest.Instructions = SkillManifest.ExtractMarkdownBody(File.ReadAllText(skillMdPath));
                return manifest;
            }
            if (File.Exists(skillMdPath))
                return SkillManifest.ParseSkillMd(File.ReadAllText(skillMdPath), Path.GetFileName(dir));
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public bool IsEnabled(LoadedSkill skill, SkillsConfig config) => !config.DisabledSkills.Contains(skill.Manifest.Id);

    // ─────────────────────────── 指令技能（SKILL.md） ───────────────────────────

    /// <summary>启用且带指令正文的技能。</summary>
    public IReadOnlyList<LoadedSkill> PromptSkills(SkillsConfig config) =>
        _skills.Where(s => IsEnabled(s, config) && !string.IsNullOrWhiteSpace(s.Manifest.Instructions)).ToList();

    /// <summary>构建指令技能提示块：列出技能名与描述，提示模型用 read_skill 读取完整说明。</summary>
    public string BuildPromptSkillsBlock(SkillsConfig config)
    {
        var list = PromptSkills(config);
        if (list.Count == 0) return "";
        var sb = new StringBuilder();
        sb.AppendLine("可用技能（用户/内置安装的领域知识包）：");
        foreach (var skill in list)
            sb.AppendLine($"- {skill.Manifest.Id}：{skill.Manifest.Name} —— {skill.Manifest.Description}");
        sb.Append("当用户的请求与某个技能的描述匹配时，先调用 read_skill 工具（参数 skill_id）读取该技能的完整说明，再按说明完成任务；上下文里的技能目录文件可用 read_skill 的 file 参数按需读取。");
        return sb.ToString();
    }

    /// <summary>读取技能内容（read_skill 工具实现）：默认 SKILL.md 全文；file 读取技能目录内的附属文件。</summary>
    public string ReadSkillContent(string skillId, string? file, SkillsConfig config)
    {
        var skill = _skills.FirstOrDefault(s => IsEnabled(s, config)
            && string.Equals(s.Manifest.Id, skillId.Trim(), StringComparison.OrdinalIgnoreCase));
        if (skill is null) return $"错误：技能 {skillId} 不存在或未启用";

        if (!string.IsNullOrWhiteSpace(file))
        {
            string full;
            string root;
            try
            {
                root = Path.GetFullPath(skill.Directory);
                full = Path.GetFullPath(Path.Combine(root, file.Trim()));
            }
            catch (Exception ex)
            {
                return $"错误：文件路径无效（{ex.Message}）";
            }
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return "错误：文件路径必须位于技能目录内";
            if (!File.Exists(full)) return $"错误：技能目录中不存在文件 {file}";
            try
            {
                return Truncate(File.ReadAllText(full), MaxSkillDocChars);
            }
            catch (IOException ex)
            {
                return $"错误：读取失败：{ex.Message}";
            }
        }

        var content = skill.Manifest.Instructions;
        if (string.IsNullOrWhiteSpace(content))
            return $"技能「{skill.Manifest.Name}」没有指令正文（可能为纯工具型技能，直接调用其工具即可）";
        return Truncate(content, MaxSkillDocChars);
    }

    // ─────────────────────────── 工具定义 ───────────────────────────

    public IReadOnlyList<ChatToolDefinition> BuildToolDefinitions(SkillsConfig config) =>
        _skills
            .Where(s => IsEnabled(s, config) && s.Manifest.Type == "http")
            .SelectMany(s => s.Manifest.Tools.Select(t => new ChatToolDefinition(t.Name, t.Description, BuildParametersJson(t))))
            .ToList();

    private static string BuildParametersJson(SkillToolSpec tool)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var p in tool.Params)
        {
            properties[p.Name] = new JsonObject
            {
                ["type"] = string.Equals(p.Type, "integer", StringComparison.OrdinalIgnoreCase) ? "integer" : "string",
                ["description"] = p.Description,
            };
            if (p.Required) required.Add(p.Name);
        }
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required,
        }.ToJsonString();
    }

    // ─────────────────────────── 工具执行 ───────────────────────────

    public async Task<string> ExecuteToolAsync(string toolName, string argumentsJson, SkillsConfig config, CancellationToken ct = default)
    {
        var found = FindTool(toolName, config);
        if (found is null) return $"错误：未找到工具 {toolName}（技能可能未启用）";
        var (skill, tool) = found.Value;

        Dictionary<string, string> args;
        try
        {
            args = ParseArguments(argumentsJson);
        }
        catch (JsonException ex)
        {
            return $"错误：工具参数不是合法 JSON（{ex.Message}）";
        }

        foreach (var p in tool.Params.Where(p => p.Required))
        {
            if (!args.TryGetValue(p.Name, out var value) || string.IsNullOrWhiteSpace(value))
                return $"错误：缺少必填参数 {p.Name}（{p.Description}）";
        }

        try
        {
            var url = skill.Manifest.BaseUrl.TrimEnd('/') + Substitute(tool.Path, args, skill, config);
            using var request = new HttpRequestMessage(new HttpMethod(tool.Method.ToUpperInvariant()), url);
            foreach (var (key, value) in tool.Headers)
                request.Headers.TryAddWithoutValidation(key, Substitute(value, args, skill, config));
            if (tool.BodyTemplate is { } bodyTemplate)
            {
                var node = JsonNode.Parse(bodyTemplate.GetRawText()) ?? new JsonObject();
                var substituted = SubstituteNode(node, args, skill, config);
                request.Content = new StringContent(substituted.ToJsonString(JsonOptions), Encoding.UTF8, "application/json");
            }

            var timeout = TimeSpan.FromSeconds(GetIntSetting(skill, config, "timeoutSeconds", 360));
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            using var resp = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var status = (int)resp.StatusCode;
            var contentType = resp.Content.Headers.ContentType?.MediaType ?? "";
            var raw = contentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase)
                ? await ReadSseAsync(resp, cts.Token)
                : await resp.Content.ReadAsStringAsync(cts.Token);
            var result = Truncate(raw, MaxResultChars);
            Log($"[{skill.Manifest.Id}] {tool.Name} → HTTP {status}（{result.Length} 字符）");
            return status is >= 200 and < 300 ? result : $"HTTP {status}：{result}";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return $"错误：工具 {tool.Name} 超时或已取消";
        }
        catch (Exception ex)
        {
            return $"错误：工具 {tool.Name} 执行失败：{ex.Message}";
        }
    }

    private (LoadedSkill Skill, SkillToolSpec Tool)? FindTool(string toolName, SkillsConfig config)
    {
        foreach (var skill in _skills.Where(s => IsEnabled(s, config)))
        {
            foreach (var tool in skill.Manifest.Tools)
            {
                if (string.Equals(tool.Name, toolName, StringComparison.Ordinal))
                    return (skill, tool);
            }
        }
        return null;
    }

    private static Dictionary<string, string> ParseArguments(string argumentsJson)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(argumentsJson)) return result;
        using var doc = JsonDocument.Parse(argumentsJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            result[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                ? prop.Value.GetString() ?? ""
                : prop.Value.GetRawText();
        }
        return result;
    }

    // ─────────────────────────── 占位符替换 ───────────────────────────

    private static JsonNode? SubstituteNode(JsonNode? node, IReadOnlyDictionary<string, string> args,
        LoadedSkill skill, SkillsConfig config)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                    obj[key] = SubstituteNode(obj[key], args, skill, config);
                return obj;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                    arr[i] = SubstituteNode(arr[i], args, skill, config);
                return arr;
            case JsonValue value when value.TryGetValue<string>(out var text):
                return JsonValue.Create(Substitute(text, args, skill, config));
            default:
                return node;
        }
    }

    private static string Substitute(string text, IReadOnlyDictionary<string, string> args,
        LoadedSkill skill, SkillsConfig config)
    {
        foreach (var (key, value) in args)
            text = text.Replace("{" + key + "}", value, StringComparison.Ordinal);
        foreach (var setting in skill.Manifest.Settings)
            text = text.Replace("{setting." + setting.Key + "}", ResolveSetting(skill, config, setting), StringComparison.Ordinal);
        return text;
    }

    /// <summary>解析技能设置：用户配置 > 清单默认值。</summary>
    public static string ResolveSetting(LoadedSkill skill, SkillsConfig config, SkillSettingSpec spec)
        => config.Settings.TryGetValue(skill.Manifest.Id, out var map)
           && map.TryGetValue(spec.Key, out var value)
           && !string.IsNullOrEmpty(value)
            ? value
            : spec.Default;

    private static int GetIntSetting(LoadedSkill skill, SkillsConfig config, string key, int fallback)
    {
        var spec = skill.Manifest.Settings.FirstOrDefault(s => s.Key == key);
        if (spec is null) return fallback;
        return int.TryParse(ResolveSetting(skill, config, spec), out var value) ? value : fallback;
    }

    // ─────────────────────────── SSE 聚合 ───────────────────────────

    /// <summary>
    /// 聚合 text/event-stream：取内容增量（兼容 OpenAI delta / 常见 content/text 字段），
    /// error 事件转为错误文本返回给模型（如 LogShare 云端未订阅等状态）。
    /// </summary>
    private static async Task<string> ReadSseAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var content = new StringBuilder();
        var errors = new List<string>();
        var statuses = new List<string>();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? eventName = null;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventName = line[6..].Trim();
                continue;
            }
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim();
            if (data.Length == 0 || data == "[DONE]") continue;

            if (eventName == "error")
            {
                var message = ExtractText(data);
                errors.Add(message.Length > 0 ? message : data);
                continue;
            }
            if (eventName == "status")
            {
                var status = ExtractStatus(data);
                if (status.Length > 0) statuses.Add(status);
                continue;
            }
            content.Append(ExtractText(data));
        }

        if (content.Length > 0) return content.ToString();
        if (errors.Count > 0) return "云端返回错误：" + string.Join("；", errors);
        if (statuses.Count > 0) return "（云端仍在排队，未返回内容：" + string.Join("；", statuses) + "）";
        return "（流式响应中没有可提取的文本）";
    }

    /// <summary>从 SSE data 中提取文本增量（兼容 OpenAI choices[].delta.content 与常见字段）。</summary>
    private static string ExtractText(string dataJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(dataJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return "";
            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0)
            {
                var first = choices[0];
                if (first.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object
                    && delta.TryGetProperty("content", out var deltaContent) && deltaContent.ValueKind == JsonValueKind.String)
                    return deltaContent.GetString() ?? "";
                if (first.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    return text.GetString() ?? "";
            }
            foreach (var key in new[] { "delta", "content", "text", "message", "error" })
            {
                if (root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                    return value.GetString() ?? "";
            }
            return "";
        }
        catch (JsonException)
        {
            return dataJson; // 非 JSON：原样保留
        }
    }

    private static string ExtractStatus(string dataJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(dataJson);
            if (doc.RootElement.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
                return type.GetString() ?? "";
        }
        catch (JsonException)
        {
        }
        return "";
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "\n…（结果已截断）";

    private void Log(string line) => SkillLog?.Invoke(this, line);
}
