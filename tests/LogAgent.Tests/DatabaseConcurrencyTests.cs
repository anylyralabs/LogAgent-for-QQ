using System.Collections.Concurrent;
using LogAgent.Memory;
using Xunit;

namespace LogAgent.Tests;

// ─────────────────────────── SQLite 并发配置（WAL + busy_timeout） ───────────────────────────

public class DatabaseConcurrencyTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"la-conc-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            try { File.Delete(_dbPath + suffix); } catch (IOException) { }
    }

    [Fact]
    public void Open_EnablesWal()
    {
        var db = new Database(_dbPath);
        db.EnsureCreated();
        using var conn = db.Open();
        using var mode = conn.CreateCommand();
        mode.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", mode.ExecuteScalar()!.ToString());
    }

    [Fact]
    public void ParallelWriters_AllSucceed()
    {
        var db = new Database(_dbPath);
        db.EnsureCreated();
        using (var seed = db.Open())
        using (var cmd = seed.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO sessions (id, chat_id, chat_type, start_time) VALUES ('s1','c','Group','t')";
            cmd.ExecuteNonQuery();
        }

        const int writers = 8, perWriter = 25;
        var errors = new ConcurrentBag<Exception>();
        Task.WaitAll(Enumerable.Range(0, writers).Select(w => Task.Run(() =>
        {
            try
            {
                for (var i = 0; i < perWriter; i++)
                {
                    using var conn = db.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText =
                        "INSERT INTO raw_messages (session_id, message_id, sender_id, sender_name, content, ts) " +
                        "VALUES ('s1', @mid, 'u', 'n', 'c', 't')";
                    cmd.Parameters.AddWithValue("@mid", $"m-{w}-{i}");
                    cmd.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        })).ToArray());

        Assert.Empty(errors);
        using var check = db.Open();
        using var count = check.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM raw_messages";
        Assert.Equal(writers * perWriter, Convert.ToInt64(count.ExecuteScalar()));
    }

    [Fact]
    public async Task BusyTimeout_WaitsForLockInsteadOfFailing()
    {
        var db = new Database(_dbPath);
        db.EnsureCreated();

        using var holder = db.Open();
        using var tx = holder.BeginTransaction();
        using (var seed = holder.CreateCommand())
        {
            seed.Transaction = tx;
            seed.CommandText = "INSERT INTO sessions (id, chat_id, chat_type, start_time) VALUES ('s1','c','Group','t')";
            seed.ExecuteNonQuery(); // 持有写锁
        }

        // 另一个连接立即写：有 busy_timeout 应等待持锁方提交，而不是立刻 SQLITE_BUSY
        var second = Task.Run(() =>
        {
            using var conn2 = db.Open();
            using var cmd = conn2.CreateCommand();
            cmd.CommandText = "INSERT INTO sessions (id, chat_id, chat_type, start_time) VALUES ('s2','c','Group','t')";
            cmd.ExecuteNonQuery();
        });

        await Task.Delay(500); // 确保第二个连接已进入等待锁状态
        tx.Commit();
        await second; // 不应抛 "database is locked"
    }
}
