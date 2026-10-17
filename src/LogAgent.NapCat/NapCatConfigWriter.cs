using System.Text.Json;
using System.Text.Json.Nodes;

namespace LogAgent.NapCat;

/// <summary>
/// NapCat 反向 WS 配置写入器：向 NapCat 的 config 目录补入/更新 LogAgent 的反向 WebSocket 客户端，
/// 覆盖模板 onebot11.json 及所有已登录账号的 onebot11_&lt;QQ&gt;.json（登录后 NapCat 从模板复制，两边都写保证生效）。
/// </summary>
public static class NapCatConfigWriter
{
    public const string ClientName = "LogAgent";

    /// <summary>
    /// 在 NapCat 配置目录内 upsert 一条反向 WS 客户端。返回已更新的文件列表（空=无文件可写）。
    /// </summary>
    /// <exception cref="InvalidOperationException">目录不存在或非法。</exception>
    public static List<string> EnsureReverseWs(string napCatDirectory, int port, string token)
    {
        if (string.IsNullOrWhiteSpace(napCatDirectory) || !Directory.Exists(napCatDirectory))
            throw new InvalidOperationException($"NapCat 目录不存在：{napCatDirectory}");

        var configDir = Path.Combine(napCatDirectory, "config");
        Directory.CreateDirectory(configDir);

        var targets = new List<string> { Path.Combine(configDir, "onebot11.json") };
        targets.AddRange(Directory.EnumerateFiles(configDir, "onebot11_*.json"));

        var updated = new List<string>();
        foreach (var file in targets)
        {
            try
            {
                if (UpsertClient(file, port, token))
                    updated.Add(file);
            }
            catch (JsonException)
            {
                // 损坏的配置文件跳过（不覆盖用户数据）
            }
        }
        return updated;
    }

    private static bool UpsertClient(string file, int port, string token)
    {
        JsonObject root;
        if (File.Exists(file))
        {
            var text = File.ReadAllText(file);
            root = string.IsNullOrWhiteSpace(text)
                ? new JsonObject()
                : JsonNode.Parse(text) as JsonObject ?? new JsonObject();
        }
        else
        {
            root = new JsonObject();
        }

        if (root["network"] is not JsonObject network)
        {
            network = new JsonObject();
            root["network"] = network;
        }
        if (network["websocketClients"] is not JsonArray clients)
        {
            clients = new JsonArray();
            network["websocketClients"] = clients;
        }

        var url = $"ws://127.0.0.1:{port}/onebot/v11/ws";
        var entry = new JsonObject
        {
            ["name"] = ClientName,
            ["enable"] = true,
            ["url"] = url,
            ["messagePostFormat"] = "array",
            ["reportSelfMessage"] = false,
            ["token"] = token,
            ["debug"] = false,
            ["heartInterval"] = 30000,
            ["reconnectInterval"] = 5000,
            ["verifyCertificate"] = true,
        };

        // 按 name 匹配既有条目则更新，否则新增
        var replaced = false;
        for (var i = 0; i < clients.Count; i++)
        {
            if (clients[i] is JsonObject c &&
                string.Equals(c["name"]?.GetValue<string>(), ClientName, StringComparison.OrdinalIgnoreCase))
            {
                clients[i] = entry;
                replaced = true;
                break;
            }
        }
        if (!replaced) clients.Add(entry);

        File.WriteAllText(file, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return true;
    }
}
