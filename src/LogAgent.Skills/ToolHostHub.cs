using System.Text.Json.Nodes;
using LogAgent.Mcp;
using LogAgent.Shared.Chat;
using LogAgent.Shared.Configuration;

namespace LogAgent.Skills;

/// <summary>
/// 统一工具宿主：把四类能力聚合为模型可见的工具集合并统一路由执行——
/// ① 技能 HTTP 工具（skill.json）；② 指令技能读取（read_skill，SKILL.md 渐进式披露）；
/// ③ MCP 服务器工具（标准协议，名为 mcp__{server}__{tool}）；
/// ④ 内置文件工具（read_file / search_file / run_shell——大日志完整落盘后的选择性读取与检索）。
/// </summary>
public sealed class ToolHostHub(SkillRegistry registry, McpManager? mcp, AppConfig appConfig, FileTools? fileTools = null, WebTools? webTools = null) : ISkillToolHost
{
    /// <summary>读取技能说明的内置工具名。</summary>
    public const string ReadSkillTool = "read_skill";

    public const string ReadSkillToolName = ReadSkillTool;

    private const string ReadSkillParameters =
        """{"type":"object","properties":{"skill_id":{"type":"string","description":"技能 id（见系统提示中的可用技能列表）"},"file":{"type":"string","description":"可选：技能目录内的附属文件名（默认读取技能说明全文）"}},"required":["skill_id"]}""";

    public IReadOnlyList<ChatToolDefinition> BuildToolDefinitions(SkillsConfig config)
    {
        var tools = new List<ChatToolDefinition>();
        tools.AddRange(registry.BuildToolDefinitions(config));

        if (registry.PromptSkills(config).Count > 0)
        {
            tools.Add(new ChatToolDefinition(ReadSkillTool,
                "读取技能的完整说明文档（SKILL.md）。当用户请求与某个技能描述匹配时，先读取再执行。",
                ReadSkillParameters));
        }

        if (mcp is not null && appConfig.Mcp.Enabled)
            tools.AddRange(mcp.GetToolDefinitions());

        if (fileTools is not null)
            tools.AddRange(fileTools.BuildToolDefinitions(shellEnabled: appConfig.Sandbox.Enabled));

        if (webTools is not null)
            tools.AddRange(webTools.BuildToolDefinitions());

        return tools;
    }

    public string BuildPromptSkillsBlock(SkillsConfig config) => registry.BuildPromptSkillsBlock(config);

    public async Task<string> ExecuteToolAsync(string toolName, string argumentsJson, SkillsConfig config, CancellationToken ct = default)
    {
        if (string.Equals(toolName, ReadSkillTool, StringComparison.Ordinal))
            return ExecuteReadSkill(argumentsJson, config);

        if (toolName.StartsWith(McpProtocol.ToolPrefix, StringComparison.Ordinal))
        {
            if (mcp is null || !appConfig.Mcp.Enabled)
                return "错误：MCP 功能未启用";
            return await mcp.ExecuteToolAsync(toolName, argumentsJson, ct);
        }

        if (fileTools is not null && FileTools.IsBuiltinTool(toolName))
            return await fileTools.ExecuteAsync(toolName, argumentsJson, ct);

        if (webTools is not null && WebTools.IsBuiltinTool(toolName))
            return await webTools.ExecuteAsync(toolName, argumentsJson, ct);

        return await registry.ExecuteToolAsync(toolName, argumentsJson, config, ct);
    }

    private string ExecuteReadSkill(string argumentsJson, SkillsConfig config)
    {
        string skillId;
        string? file = null;
        try
        {
            var node = JsonNode.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson) as JsonObject;
            skillId = node?["skill_id"]?.GetValue<string>() ?? "";
            var fileNode = node?["file"];
            if (fileNode is not null && fileNode.GetValueKind() == System.Text.Json.JsonValueKind.String)
            {
                var value = fileNode.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(value)) file = value;
            }
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return "错误：read_skill 参数不是合法 JSON（应为 {\"skill_id\":\"…\"}）";
        }
        if (string.IsNullOrWhiteSpace(skillId))
            return "错误：缺少必填参数 skill_id";
        return registry.ReadSkillContent(skillId, file, config);
    }
}
