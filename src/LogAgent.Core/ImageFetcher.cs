using System.Net.Http;

namespace LogAgent.Core;

/// <summary>
/// 图片下载器：把消息中的图片 URL 下载并转为 data URI（供多模态模型读图）。
/// 单图上限 4MB；失败/超限返回说明文本（由调用方并入上下文，模型可如实转述）。
/// </summary>
public sealed class ImageFetcher
{
    /// <summary>图片大小上限（超过则跳过并说明）。</summary>
    public const long MaxImageBytes = 4 * 1024 * 1024;

    /// <summary>单条消息最多读取的图片数。</summary>
    public const int MaxImages = 4;

    private readonly HttpClient _http;

    public ImageFetcher(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>下载图片转 data URI；失败/超限返回 (null, 说明)。</summary>
    public async Task<(string? DataUri, string? Error)> FetchAsDataUriAsync(string url, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            if (resp.Content.Headers.ContentLength is { } len && len > MaxImageBytes)
                return (null, $"图片过大（{FormatSize(len)}，超过 {FormatSize(MaxImageBytes)} 上限）");
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length == 0) return (null, "图片内容为空");
            if (bytes.Length > MaxImageBytes)
                return (null, $"图片过大（{FormatSize(bytes.Length)}，超过 {FormatSize(MaxImageBytes)} 上限）");

            var mime = resp.Content.Headers.ContentType?.MediaType;
            if (string.IsNullOrEmpty(mime) || !mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                mime = GuessMime(bytes);
            return ($"data:{mime};base64,{Convert.ToBase64String(bytes)}", null);
        }
        catch (Exception ex)
        {
            return (null, $"下载失败：{ex.Message}");
        }
    }

    /// <summary>按魔数猜图片类型（服务器未给 Content-Type 时兜底）。</summary>
    public static string GuessMime(byte[] b)
    {
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xD8) return "image/jpeg";
        if (b.Length >= 4 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return "image/png";
        if (b.Length >= 3 && b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46) return "image/gif";
        if (b.Length >= 12 && b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50) return "image/webp";
        return "image/jpeg";
    }

    private static string FormatSize(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / 1024.0 / 1024:F1}MB" : $"{bytes / 1024.0:F0}KB";
}
