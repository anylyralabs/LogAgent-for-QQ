namespace LogAgent.Mcp;

/// <summary>内置 MCP 目录模板（一键添加到配置，参数可按需修改）。</summary>
public sealed record McpCatalogTemplate(
    string Name, string Description, string Transport, string Command, string Args, string EnvHint = "");

/// <summary>
/// 内置 MCP 服务器目录：常用官方/社区服务器模板（无需联网即可选择）。
/// 添加后会进入「MCP 服务器」列表，可编辑参数后再启用。
/// </summary>
public static class McpCatalog
{
    public static IReadOnlyList<McpCatalogTemplate> Builtin { get; } =
    [
        new("文件系统", "读写本机指定目录的文件（把参数里的路径改成要授权的目录）",
            "stdio", "npx", "-y @modelcontextprotocol/server-filesystem C:\\"),
        new("Memory 记忆", "基于知识图谱的长期记忆存储与检索（无需参数）",
            "stdio", "npx", "-y @modelcontextprotocol/server-memory"),
        new("Everything 演示", "官方测试服务器（echo/加法/长任务等示例工具；验证链路用）",
            "stdio", "npx", "-y @modelcontextprotocol/server-everything"),
        new("Sequential Thinking", "结构化分步推理（复杂问题拆解为思考链）",
            "stdio", "npx", "-y @modelcontextprotocol/server-sequential-thinking"),
        new("SQLite", "查询 SQLite 数据库（把 --db-path 改成数据库文件路径）",
            "stdio", "npx", "-y @modelcontextprotocol/server-sqlite --db-path C:\\path\\to\\db.sqlite"),
        new("GitHub", "GitHub 仓库 / Issue / PR 操作（需在环境变量填 GITHUB_PERSONAL_ACCESS_TOKEN）",
            "stdio", "npx", "-y @modelcontextprotocol/server-github", "GITHUB_PERSONAL_ACCESS_TOKEN=ghp_xxx"),
        new("Fetch 网页抓取", "抓取网页并转为 markdown（需要 Python + uv 环境）",
            "stdio", "uvx", "mcp-server-fetch"),
        new("Puppeteer 浏览器", "浏览器自动化（打开网页 / 截图 / 点击等）",
            "stdio", "npx", "-y @modelcontextprotocol/server-puppeteer"),
    ];
}
