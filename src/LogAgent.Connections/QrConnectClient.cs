using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LogAgent.Connections;

public enum QrBindStatus { Pending, Completed, Expired }

public sealed record QrBindResult(QrBindStatus Status, string? AppId = null, string? AppSecret = null, string? UserOpenId = null);

/// <summary>
/// QQ 官方机器人扫码绑定客户端（协议复刻自官方 SDK @tencent-connect/qqbot-connector v1.2.0）：
/// 1. POST /lite/create_bind_task {key} → task_id（key 为 32 字节随机数，base64）
/// 2. 二维码内容：/qqbot/openclaw/connect.html?task_id=…&source=…&_wv=2
/// 3. 每 2 秒 POST /lite/poll_bind_result {task_id}，status: 0 NONE / 1 PENDING / 2 COMPLETED / 3 EXPIRED
/// 4. COMPLETED 时 bot_encrypt_secret 为 base64([12B IV][密文][16B GCM tag])，用 key 做 AES-256-GCM 解密得 AppSecret。
/// </summary>
public sealed class QrConnectClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private const string Base = "https://q.qq.com";

    private byte[]? _key;

    /// <summary>创建绑定任务，返回二维码内容 URL（由调用方渲染成二维码图片）。</summary>
    public async Task<string> StartAsync(string source = "", CancellationToken ct = default)
    {
        _key = RandomNumberGenerator.GetBytes(32);
        var resp = await PostAsync("/lite/create_bind_task",
            new { key = Convert.ToBase64String(_key) }, ct);
        var taskId = resp.GetProperty("data").GetProperty("task_id").GetString()
            ?? throw new InvalidOperationException("create_bind_task：缺少 task_id");
        _taskId = taskId;
        return $"{Base}/qqbot/openclaw/connect.html?task_id={Uri.EscapeDataString(taskId)}&source={Uri.EscapeDataString(source)}&_wv=2";
    }

    /// <summary>轮询一次绑定结果（调用方每 ~2 秒调一次）。</summary>
    public async Task<QrBindResult> PollAsync(CancellationToken ct = default)
    {
        if (_taskId is null || _key is null) throw new InvalidOperationException("请先调用 StartAsync");
        var resp = await PostAsync("/lite/poll_bind_result", new { task_id = _taskId }, ct);
        var data = resp.GetProperty("data");
        var status = data.TryGetProperty("status", out var s) ? s.GetInt32() : 0;
        return status switch
        {
            2 => new QrBindResult(QrBindStatus.Completed,
                GetString(data, "bot_appid"),
                DecryptSecret(GetString(data, "bot_encrypt_secret")),
                GetString(data, "user_openid")),
            3 => new QrBindResult(QrBindStatus.Expired),
            _ => new QrBindResult(QrBindStatus.Pending),
        };
    }

    private string? _taskId;

    private async Task<JsonElement> PostAsync(string path, object body, CancellationToken ct)
    {
        using var resp = await Http.PostAsJsonAsync(Base + path, body, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement.Clone();
        var retcode = root.TryGetProperty("retcode", out var rc) ? rc.GetInt32() : -1;
        if (retcode != 0)
            throw new InvalidOperationException($"接口 {path} 返回错误：{GetString(root, "msg") ?? $"retcode={retcode}"}");
        return root;
    }

    private static string? GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>AES-256-GCM 解密：payload = base64([12B IV][密文][16B tag])，key 为 32 字节。</summary>
    private string DecryptSecret(string? encryptB64)
    {
        if (string.IsNullOrEmpty(encryptB64)) return "";
        var payload = Convert.FromBase64String(encryptB64);
        var iv = payload.AsSpan(0, 12);
        var tag = payload.AsSpan(payload.Length - 16);
        var cipher = payload.AsSpan(12, payload.Length - 28);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(_key!, 16);
        aes.Decrypt(iv, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }
}
