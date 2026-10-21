namespace LogAgent.Shared.Configuration;

/// <summary>OneBot11 反向 WS 服务端配置（我方为服务端，NapCat 主动连入）。</summary>
public sealed class OneBot11Config
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 6701;
    public string Path { get; set; } = "/onebot/v11/ws";
    public string Token { get; set; } = "";
}

/// <summary>NapCat 托管配置。</summary>
public sealed class NapCatConfig
{
    public bool AutoStart { get; set; }

    /// <summary>NapCat 启动脚本路径（如 napcat.bat / launcher.bat / .exe），工作目录取其所在目录。</summary>
    public string ScriptPath { get; set; } = "";

    /// <summary>脚本所在目录（派生值，供 webui.json 定位等使用；旧配置兼容字段）。</summary>
    public string Directory { get; set; } = "";

    public int WebUiPort { get; set; } = 6099;
    public string WebUiToken { get; set; } = "";

    /// <summary>解析生效目录：优先 ScriptPath 所在目录，其次旧 Directory 字段。</summary>
    public string ResolveDirectory() =>
        !string.IsNullOrWhiteSpace(ScriptPath)
            ? Path.GetDirectoryName(Path.GetFullPath(ScriptPath)) ?? Directory
            : Directory;
}

/// <summary>QQ 官方机器人配置（开放平台）。</summary>
public sealed class OfficialBotConfig
{
    public bool Enabled { get; set; }
    public string AppId { get; set; } = "";
    public string AppSecret { get; set; } = "";
    public bool Sandbox { get; set; }

    /// <summary>
    /// 使用 Markdown 消息（<c>msg_type=2</c>）。平台拒绝时自动回退纯文本，
    /// 因此默认开启也不会导致消息发不出去。
    /// </summary>
    public bool MarkdownSupport { get; set; } = true;
}

/// <summary>NapCat WebUI 配置文件片段（webui.json，用于自动提取 token）。</summary>
public sealed class NapCatWebUiFile
{
    public string Token { get; set; } = "";
    public int Port { get; set; } = 6099;
}
