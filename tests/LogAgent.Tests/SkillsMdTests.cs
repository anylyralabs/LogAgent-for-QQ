using LogAgent.Shared.Configuration;
using LogAgent.Skills;

namespace LogAgent.Tests;

public class SkillMdTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"la-skillmd-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void ParseSkillMd_FrontmatterAndBody()
    {
        const string md = """
            ---
            name: logshare-deploy
            description: 部署 LogShare 服务的完整指南
            version: 1.2.0
            author: 柠枺
            ---

            # 部署指南

            第一步：克隆仓库……
            """;
        var manifest = SkillManifest.ParseSkillMd(md, "fallback-id");
        Assert.NotNull(manifest);
        Assert.Equal("logshare-deploy", manifest!.Id);
        Assert.Equal("logshare-deploy", manifest.Name);
        Assert.Equal("部署 LogShare 服务的完整指南", manifest.Description);
        Assert.Equal("1.2.0", manifest.Version);
        Assert.Equal("柠枺", manifest.Author);
        Assert.Equal("prompt", manifest.Type);
        Assert.Contains("部署指南", manifest.Instructions);
        Assert.DoesNotContain("---", manifest.Instructions); // frontmatter 已剥离
    }

    [Fact]
    public void ParseSkillMd_NoFrontmatter_FallsBackToDirIdAndFirstLine()
    {
        var manifest = SkillManifest.ParseSkillMd("# 我的技能\n\n这是正文", "my-skill");
        Assert.NotNull(manifest);
        Assert.Equal("my-skill", manifest.Id);
        Assert.Equal("我的技能", manifest.Description);
        Assert.Contains("这是正文", manifest.Instructions);
    }

    [Fact]
    public void ParseSkillMd_Empty_ReturnsNull()
    {
        Assert.Null(SkillManifest.ParseSkillMd("", "x"));
        Assert.Null(SkillManifest.ParseSkillMd("---\nname: only-name\n---\n", "x-forced"));
    }

    [Fact]
    public void ExtractMarkdownBody_StripsFrontmatter()
    {
        var body = SkillManifest.ExtractMarkdownBody("---\nname: a\n---\n正文内容");
        Assert.Equal("正文内容", body);
    }

    [Fact]
    public void LoadSkillDir_SkillMdOnly_LoadsPromptSkill()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "SKILL.md"),
                "---\nname: demo-skill\ndescription: 演示用\n---\n正文说明");
            var manifest = SkillRegistry.LoadSkillDir(dir);
            Assert.NotNull(manifest);
            Assert.Equal("demo-skill", manifest!.Id);
            Assert.Equal("prompt", manifest.Type);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void LoadSkillDir_SkillJsonWithSkillMd_AttachesInstructions()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "skill.json"), """
                {"id":"http-tool","name":"HTTP 技能","type":"http","baseUrl":"https://example.com",
                 "tools":[{"name":"t1","description":"d","method":"GET","path":"/x"}]}
                """);
            File.WriteAllText(Path.Combine(dir, "SKILL.md"), "---\nname: http-tool\n---\n使用补充说明");
            var manifest = SkillRegistry.LoadSkillDir(dir);
            Assert.NotNull(manifest);
            Assert.Equal("http", manifest!.Type);
            Assert.Single(manifest.Tools);
            Assert.Equal("使用补充说明", manifest.Instructions);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void LoadSkillDir_Empty_ReturnsNull()
    {
        var dir = NewTempDir();
        try
        {
            Assert.Null(SkillRegistry.LoadSkillDir(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

public class ToolHostHubTests
{
    private static (ToolHostHub Hub, SkillsConfig Config, string Dir) MakeHub(bool withPromptSkill)
    {
        var builtinDir = Path.Combine(Path.GetTempPath(), $"la-hub-builtin-{Guid.NewGuid():N}");
        var userDir = Path.Combine(Path.GetTempPath(), $"la-hub-user-{Guid.NewGuid():N}");
        Directory.CreateDirectory(userDir);
        if (withPromptSkill)
        {
            var skillDir = Path.Combine(userDir, "demo");
            Directory.CreateDirectory(skillDir);
            File.WriteAllText(Path.Combine(skillDir, "SKILL.md"),
                "---\nname: demo\ndescription: 演示技能：处理演示任务\n---\n# 演示说明\n\n按这三步处理。");
        }
        var registry = new SkillRegistry(builtinDir, userDir);
        registry.Reload();
        var hub = new ToolHostHub(registry, null, new AppConfig());
        return (hub, new SkillsConfig(), userDir);
    }

    [Fact]
    public async Task ReadSkill_ReturnsFullInstructions()
    {
        var (hub, config, dir) = MakeHub(withPromptSkill: true);
        try
        {
            var result = await hub.ExecuteToolAsync("read_skill", """{"skill_id":"demo"}""", config);
            Assert.Contains("按这三步处理", result);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ReadSkill_FileOutsideDir_Rejected()
    {
        var (hub, config, dir) = MakeHub(withPromptSkill: true);
        try
        {
            var result = await hub.ExecuteToolAsync("read_skill",
                """{"skill_id":"demo","file":"../../secret.txt"}""", config);
            Assert.Contains("错误", result);
            Assert.Contains("技能目录内", result);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ReadSkill_UnknownSkill_ReturnsError()
    {
        var (hub, config, dir) = MakeHub(withPromptSkill: false);
        try
        {
            var result = await hub.ExecuteToolAsync("read_skill", """{"skill_id":"nope"}""", config);
            Assert.Contains("不存在或未启用", result);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task McpPrefix_WithoutManager_ReturnsFriendlyError()
    {
        var (hub, config, dir) = MakeHub(withPromptSkill: false);
        try
        {
            var result = await hub.ExecuteToolAsync("mcp__x__y", "{}", config);
            Assert.Contains("MCP 功能未启用", result);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void BuildToolDefinitions_IncludesReadSkillOnlyWhenPromptSkillsExist()
    {
        var (hubWith, config1, dir1) = MakeHub(withPromptSkill: true);
        var (hubWithout, config2, dir2) = MakeHub(withPromptSkill: false);
        try
        {
            Assert.Contains(hubWith.BuildToolDefinitions(config1), t => t.Name == "read_skill");
            Assert.DoesNotContain(hubWithout.BuildToolDefinitions(config2), t => t.Name == "read_skill");

            var block = hubWith.BuildPromptSkillsBlock(config1);
            Assert.Contains("demo", block);
            Assert.Contains("演示技能", block);
            Assert.Equal("", hubWithout.BuildPromptSkillsBlock(config2));
        }
        finally
        {
            Directory.Delete(dir1, recursive: true);
            Directory.Delete(dir2, recursive: true);
        }
    }
}
