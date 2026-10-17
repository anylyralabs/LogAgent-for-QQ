using System.Net.Http.Json;
using System.Text.Json;
using LogAgent.Shared.Configuration;

namespace LogAgent.Routing;

/// <summary>OpenAI 兼容 /embeddings 客户端。</summary>
public sealed class OpenAiEmbeddingsClient(ProviderConfig config, HttpClient? http = null)
{
    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var url = $"{config.Endpoint.TrimEnd('/')}/embeddings";
        using var resp = await _http.PostAsJsonAsync(url, new
        {
            model = config.EmbeddingModel ?? config.ChatModel,
            input = text,
        }, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var data = doc.RootElement.GetProperty("data")[0].GetProperty("embedding");
        var result = new float[data.GetArrayLength()];
        var i = 0;
        foreach (var v in data.EnumerateArray()) result[i++] = v.GetSingle();
        return result;
    }
}
