namespace LogAgent.Shared.Configuration;

/// <summary>
/// 技能（可扩展能力）配置：总开关、商店目录地址、禁用列表、每技能设置项。
/// 技能 = 自带/安装的扩展包（skill.json），向模型暴露可调用的工具（如 LogShare 云端日志分析）。
/// </summary>
public sealed class SkillsConfig
{
    /// <summary>技能总开关（关闭后不向模型暴露任何技能工具）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>技能商店目录地址（catalog.json，可留空仅用内置与本地安装）。</summary>
    public string CatalogUrl { get; set; } = "";

    /// <summary>已禁用的技能 Id（默认全部启用）。</summary>
    public List<string> DisabledSkills { get; set; } = [];

    /// <summary>每技能的用户设置（skillId → key → value；覆盖 skill.json 默认值）。</summary>
    public Dictionary<string, Dictionary<string, string>> Settings { get; set; } = new();
}
