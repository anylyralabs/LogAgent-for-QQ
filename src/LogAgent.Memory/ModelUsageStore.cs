using LogAgent.Routing;

namespace LogAgent.Memory;

/// <summary>
/// 模型用量持久化：按「日期 × Provider × 模型 × 任务」聚合（upsert 累加），跨重启累计。
/// 用途：盯免费额度（如 Space Bunny）、看各任务调用量与 token 消耗。
/// 记录失败绝不影响主流程（内部全兜底）。
/// </summary>
public sealed class ModelUsageStore(string dbPath)
{
    private readonly Database _db = new(dbPath);
    private int _ensured;

    private void EnsureTable()
    {
        if (Interlocked.Exchange(ref _ensured, 1) == 1) return;
        try
        {
            using var conn = _db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS model_usage (
                    date              TEXT NOT NULL,
                    provider          TEXT NOT NULL,
                    model             TEXT NOT NULL,
                    task              TEXT NOT NULL,
                    calls             INTEGER NOT NULL DEFAULT 0,
                    ok                INTEGER NOT NULL DEFAULT 0,
                    fail              INTEGER NOT NULL DEFAULT 0,
                    prompt_tokens     INTEGER NOT NULL DEFAULT 0,
                    completion_tokens INTEGER NOT NULL DEFAULT 0,
                    latency_ms        INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (date, provider, model, task)
                );
                """;
            cmd.ExecuteNonQuery();
        }
        catch (Exception)
        {
            Interlocked.Exchange(ref _ensured, 0); // 下次重试建表
        }
    }

    public void Record(RouterMeterEvent e)
    {
        try
        {
            EnsureTable();
            using var conn = _db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO model_usage(date, provider, model, task, calls, ok, fail, prompt_tokens, completion_tokens, latency_ms)
                VALUES ($d, $p, $m, $t, 1, $ok, $fail, $pt, $ct, $lat)
                ON CONFLICT(date, provider, model, task) DO UPDATE SET
                    calls = calls + 1,
                    ok = ok + $ok,
                    fail = fail + $fail,
                    prompt_tokens = prompt_tokens + $pt,
                    completion_tokens = completion_tokens + $ct,
                    latency_ms = latency_ms + $lat
                """;
            cmd.Parameters.AddWithValue("$d", DateTime.Now.ToString("yyyy-MM-dd"));
            cmd.Parameters.AddWithValue("$p", e.ProviderId);
            cmd.Parameters.AddWithValue("$m", e.Model);
            cmd.Parameters.AddWithValue("$t", e.TaskType.ToString());
            cmd.Parameters.AddWithValue("$ok", e.Success ? 1 : 0);
            cmd.Parameters.AddWithValue("$fail", e.Success ? 0 : 1);
            cmd.Parameters.AddWithValue("$pt", e.PromptTokens);
            cmd.Parameters.AddWithValue("$ct", e.CompletionTokens);
            cmd.Parameters.AddWithValue("$lat", (long)e.Latency.TotalMilliseconds);
            cmd.ExecuteNonQuery();
        }
        catch (Exception)
        {
            // 计量是旁路：任何异常都吞掉，不影响聊天主流程
        }
    }

    /// <summary>今日（本地日期）合计。</summary>
    public (long Calls, long Ok, long Fail, long PromptTokens, long CompletionTokens) Today()
    {
        try
        {
            EnsureTable();
            using var conn = _db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT COALESCE(SUM(calls),0), COALESCE(SUM(ok),0), COALESCE(SUM(fail),0),
                       COALESCE(SUM(prompt_tokens),0), COALESCE(SUM(completion_tokens),0)
                FROM model_usage WHERE date = $d
                """;
            cmd.Parameters.AddWithValue("$d", DateTime.Now.ToString("yyyy-MM-dd"));
            using var r = cmd.ExecuteReader();
            r.Read();
            return (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4));
        }
        catch (Exception)
        {
            return (0, 0, 0, 0, 0);
        }
    }

    /// <summary>近 N 天明细（按日×模型，倒序）。</summary>
    public List<UsageRow> LastDays(int days = 7)
    {
        try
        {
            EnsureTable();
            using var conn = _db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT date, provider, model, task, calls, ok, fail, prompt_tokens, completion_tokens, latency_ms
                FROM model_usage
                WHERE date >= $since
                ORDER BY date DESC, calls DESC
                """;
            cmd.Parameters.AddWithValue("$since", DateTime.Now.AddDays(-days).ToString("yyyy-MM-dd"));
            using var r = cmd.ExecuteReader();
            var rows = new List<UsageRow>();
            while (r.Read())
            {
                var calls = r.GetInt64(4);
                rows.Add(new UsageRow(
                    r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                    calls, r.GetInt64(5), r.GetInt64(6), r.GetInt64(7), r.GetInt64(8),
                    calls > 0 ? r.GetInt64(9) / calls : 0));
            }
            return rows;
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>用量明细行（AvgLatencyMs = 总延迟/调用次数）。</summary>
    public sealed record UsageRow(
        string Date, string Provider, string Model, string Task,
        long Calls, long Ok, long Fail, long PromptTokens, long CompletionTokens, long AvgLatencyMs);
}
