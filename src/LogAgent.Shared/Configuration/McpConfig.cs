namespace LogAgent.Shared.Configuration;

/// <summary>
/// MCP（Model Context Protocol）配置：接入标准 MCP 服务器，服务器工具自动暴露给模型。
/// </summary>
public sealed class McpConfig
{
    /// <summary>MCP 总开关（关闭后不连接任何服务器、不暴露 MCP 工具）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>已配置的 MCP 服务器（stdio 本地子进程 / Streamable HTTP 远程）。</summary>
    public List<McpServerEntry> Servers { get; set; } = [];
}

/// <summary>一个 MCP 服务器配置（对应 config.json 的 Mcp.Servers 条目）。</summary>
public sealed class McpServerEntry
{
    public string Id { get; set; } = "";

    /// <summary>显示名（也用于工具说明前缀）。</summary>
    public string Name { get; set; } = "";

    /// <summary>传输方式："stdio"（本地命令）或 "http"（Streamable HTTP）。</summary>
    public string Transport { get; set; } = "stdio";

    /// <summary>stdio：可执行命令（如 npx、python、uvx、完整 exe 路径）。</summary>
    public string Command { get; set; } = "";

    /// <summary>stdio：命令参数（一行，空格分隔，支持引号）。</summary>
    public string Args { get; set; } = "";

    /// <summary>stdio：工作目录（可留空）。</summary>
    public string WorkingDirectory { get; set; } = "";

    /// <summary>stdio：附加环境变量（如 API Key）。</summary>
    public Dictionary<string, string> Env { get; set; } = new();

    /// <summary>http：服务器 URL。</summary>
    public string Url { get; set; } = "";

    /// <summary>http：附加请求头（如 Authorization）。</summary>
    public Dictionary<string, string> Headers { get; set; } = new();

    public bool Enabled { get; set; } = true;
}
