using LogAgent.Memory;
using LogAgent.Routing;
using LogAgent.Shared.Configuration;
using Xunit;

namespace LogAgent.Tests;

// ─────────────────────────── 模型用量持久化（按日聚合） ───────────────────────────

public class ModelUsageStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"la-usage-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_dbPath + suffix); } catch (IOException) { }
    }

    [Fact]
    public void Record_AccumulatesPerDayModelAndTask()
    {
        var store = new ModelUsageStore(_dbPath);
        store.Record(new RouterMeterEvent(TaskType.Chat, "spacebunny01", "space-bunny", true, 100, 50, TimeSpan.FromMilliseconds(200)));
        store.Record(new RouterMeterEvent(TaskType.Chat, "spacebunny01", "space-bunny", true, 80, 40, TimeSpan.FromMilliseconds(100)));
        store.Record(new RouterMeterEvent(TaskType.Chat, "spacebunny01", "space-bunny", false, 10, 0, TimeSpan.FromMilliseconds(300)));
        store.Record(new RouterMeterEvent(TaskType.LogAnalysis, "hy4wb01", "hy4-preview", true, 500, 900, TimeSpan.FromMilliseconds(1000)));

        var (calls, ok, fail, pt, ct) = store.Today();
        Assert.Equal(4, calls);
        Assert.Equal(3, ok);
        Assert.Equal(1, fail);
        Assert.Equal(690, pt);
        Assert.Equal(990, ct);

        var rows = store.LastDays(7);
        Assert.Equal(2, rows.Count);
        var bunny = Assert.Single(rows, r => r.Model == "space-bunny");
        Assert.Equal(3, bunny.Calls);
        Assert.Equal(190, bunny.PromptTokens);
        Assert.Equal(200, bunny.AvgLatencyMs); // (200+100+300)/3
    }

    [Fact]
    public void FreshStore_ReturnsZeros_NotErrors()
    {
        var store = new ModelUsageStore(_dbPath);
        var (calls, ok, fail, pt, ct) = store.Today();
        Assert.Equal(0, calls);
        Assert.Equal(0, ok);
        Assert.Equal(0, fail);
        Assert.Empty(store.LastDays(7));
    }
}
