using System.Collections.Concurrent;
using LogAgent.Routing;

namespace LogAgent.Core;

/// <summary>Provider 级计量累计：调用数/成功/失败/token/平均延迟（仪表盘看板数据源）。</summary>
public sealed class RouterStats
{
    public sealed record ProviderStat(string Provider, string Model, long Ok, long Fail,
        long PromptTokens, long CompletionTokens, double AvgLatencyMs);

    private readonly ConcurrentDictionary<string, (long Ok, long Fail, long P, long C, double LatencySum, string Model)> _stats = new();

    public void Record(RouterMeterEvent e)
    {
        _stats.AddOrUpdate(e.ProviderId,
            (e.Success ? 1 : 0, e.Success ? 0 : 1, e.PromptTokens, e.CompletionTokens,
                e.Latency.TotalMilliseconds, e.Model),
            (_, v) => (v.Ok + (e.Success ? 1 : 0), v.Fail + (e.Success ? 0 : 1),
                v.P + e.PromptTokens, v.C + e.CompletionTokens,
                v.LatencySum + e.Latency.TotalMilliseconds, e.Model));
    }

    public List<ProviderStat> Snapshot() =>
        _stats.Select(kv => new ProviderStat(
            kv.Key, kv.Value.Model, kv.Value.Ok, kv.Value.Fail,
            kv.Value.P, kv.Value.C,
            kv.Value.Ok > 0 ? kv.Value.LatencySum / kv.Value.Ok : 0)).ToList();
}
