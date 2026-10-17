using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LogAgent.NapCat;

/// <summary>
/// NapCat WebUI API 客户端（默认 6099 端口）。
/// 鉴权：POST /api/auth/login，body.token = SHA256(webuiToken + ".napcat")，返回 Credential 作 Bearer。
/// 登录：GetQQLoginQrcode / CheckLoginStatus / RefreshQRcode / GetQuickLoginList / SetQuickLogin。
/// </summary>
public sealed class NapCatWebUiClient : IAsyncDisposable
{
    private readonly HttpClient _http;
    private string? _credential;

    public string BaseUrl { get; }
    public string WebUiToken { get; }

    public NapCatWebUiClient(string baseUrl, string token)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        WebUiToken = token;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public static string HashLoginToken(string webUiToken)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(webUiToken + ".napcat"));
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>登录换取 Credential（401 自动重登）。</summary>
    public async Task LoginAsync(CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync($"{BaseUrl}/api/auth/login",
            new { token = HashLoginToken(WebUiToken) }, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        // 响应包装兼容：{data:{Credential}} / {Credential} / {data:{credential}}
        _credential = FindString(root, "Credential") ?? FindString(root, "credential")
            ?? throw new InvalidOperationException("WebUI 登录响应中未找到 Credential");
    }

    private async Task<JsonElement> PostAsync(string path, object? body, CancellationToken ct)
    {
        if (_credential is null) await LoginAsync(ct);
        using var resp = await _http.SendAsync(MakeRequest(HttpMethod.Post, path, body), ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            _credential = null;
            await LoginAsync(ct);
            using var retry = await _http.SendAsync(MakeRequest(HttpMethod.Post, path, body), ct);
            retry.EnsureSuccessStatusCode();
            return ParseData(retry);
        }
        resp.EnsureSuccessStatusCode();
        return ParseData(resp);
    }

    private async Task<JsonElement> GetAsync(string path, CancellationToken ct)
    {
        if (_credential is null) await LoginAsync(ct);
        using var resp = await _http.SendAsync(MakeRequest(HttpMethod.Get, path, null), ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            _credential = null;
            await LoginAsync(ct);
            using var retry = await _http.SendAsync(MakeRequest(HttpMethod.Get, path, null), ct);
            retry.EnsureSuccessStatusCode();
            return ParseData(retry);
        }
        resp.EnsureSuccessStatusCode();
        return ParseData(resp);
    }

    private HttpRequestMessage MakeRequest(HttpMethod method, string path, object? body)
    {
        var req = new HttpRequestMessage(method, $"{BaseUrl}{path}");
        req.Headers.Authorization = new("Bearer", _credential);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }

    private static JsonElement ParseData(HttpResponseMessage resp)
    {
        using var doc = JsonDocument.Parse(resp.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        var root = doc.RootElement.Clone();
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data)
            && data.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            return data.Clone();
        return root;
    }

    private static string? FindString(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in el.EnumerateObject())
        {
            if (p.Value.ValueKind == JsonValueKind.String &&
                (p.Name == name || string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
                return p.Value.GetString();
            if (p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                var found = FindString(p.Value, name);
                if (found is not null) return found;
            }
        }
        return null;
    }

    /// <summary>获取登录二维码（返回原始 JSON，字段因版本而异：qrcode/url/base64）。</summary>
    public Task<JsonElement> GetQrCodeAsync(CancellationToken ct = default)
        => PostAsync("/api/QQLogin/GetQQLoginQrcode", new { }, ct);

    public Task<JsonElement> RefreshQrCodeAsync(CancellationToken ct = default)
        => PostAsync("/api/QQLogin/RefreshQRcode", new { }, ct);

    /// <summary>登录状态（在线状态/阶段/错误）。</summary>
    public Task<JsonElement> CheckLoginStatusAsync(CancellationToken ct = default)
        => PostAsync("/api/QQLogin/CheckLoginStatus", new { }, ct);

    /// <summary>快速登录账号列表。</summary>
    public Task<JsonElement> GetQuickLoginListAsync(CancellationToken ct = default)
        => GetAsync("/api/QQLogin/GetQuickLoginList", ct);

    /// <summary>快速登录指定 QQ。</summary>
    public Task<JsonElement> SetQuickLoginAsync(string uin, CancellationToken ct = default)
        => PostAsync("/api/QQLogin/SetQuickLogin", new { uin }, ct);

    /// <summary>当前登录账号信息。</summary>
    public Task<JsonElement> GetLoginInfoAsync(CancellationToken ct = default)
        => PostAsync("/api/QQLogin/GetQQLoginInfo", new { }, ct);

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }
}
