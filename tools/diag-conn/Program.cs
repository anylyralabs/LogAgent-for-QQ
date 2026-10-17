using LogAgent.Connections;
using LogAgent.NapCat;
using LogAgent.Shared.Configuration;

// 官方 Bot 连接诊断 + NapCat 配置注入工具
var config = AppConfigStore.Load();

// 用法：DiagConn napcat-token  —— 验证 webui.json 自动读取
if (args.Length >= 1 && args[0] == "napcat-token")
{
    var mgr = new NapCatManager(config.NapCat);
    Console.WriteLine($"script: {config.NapCat.ScriptPath}");
    Console.WriteLine($"dir: {config.NapCat.ResolveDirectory()}");
    Console.WriteLine($"WebUiToken: {mgr.WebUiToken ?? "(null)"}");
    return;
}

// 用法：DiagConn inject-napcat  —— 把反向 WS 配置补入真实 NapCat 目录
if (args.Length >= 1 && args[0] == "inject-napcat")
{
    var dir = config.NapCat.ResolveDirectory();
    Console.WriteLine($"NapCat dir: {dir}");
    var files = NapCatConfigWriter.EnsureReverseWs(dir, config.OneBot11.Port, config.OneBot11.Token);
    Console.WriteLine($"updated {files.Count} file(s):");
    foreach (var f in files) Console.WriteLine($"  {f}");
    return;
}

var ob = config.OfficialBot;
Console.WriteLine($"AppId={ob.AppId} Sandbox={ob.Sandbox} SecretLen={ob.AppSecret.Length}");

var conn = new OfficialBotConnection(ob);
conn.ConnectionLog += (_, line) => Console.WriteLine($"[log] {line}");
conn.MessageReceived += (_, e) => Console.WriteLine($"[msg] {e.Message.ChatType} {e.Message.SenderName}: {e.Message.Text[..Math.Min(60, e.Message.Text.Length)]}");

try
{
    await conn.StartAsync();
    Console.WriteLine("StartAsync OK，等待 WS 握手…");
    for (var i = 0; i < 10; i++)
    {
        await Task.Delay(1000);
        Console.WriteLine($"t={i + 1}s IsConnected={conn.IsConnected}");
        if (conn.IsConnected) break;
    }
}
catch (Exception ex)
{
    Console.WriteLine($"EXCEPTION: {ex.GetType().Name}");
    Console.WriteLine(ex.Message);
    if (ex.InnerException is not null)
        Console.WriteLine($"INNER: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
}
await conn.StopAsync();
