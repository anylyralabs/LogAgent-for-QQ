using System.Collections.Concurrent;
using LogAgent.Routing;
using LogAgent.Shared.Configuration;
using Microsoft.Data.Sqlite;

namespace LogAgent.Knowledge;

public sealed record KnowledgeItem(long Id, string Content, string Source, string? SourceRef,
    string Status, int UsageCount, string CreatedAt, string Title = "");

public sealed record KnowledgeHit(KnowledgeItem Item, double Score, string Via);

/// <summary>知识条目的来源可读化（提示词注入与 UI 共用）。</summary>
public static class KnowledgeItemExtensions
{
    public static string DescribeSource(this KnowledgeItem item) => item.Source switch
    {
        "web" => $"网页 {item.SourceRef}",
        "user_reply" => $"群友 {item.SourceRef}",
        "doc" => $"文档 {item.SourceRef}",
        "manual" => "手动录入",
        _ => item.Source,
    };
}

/// <summary>
/// 知识库（规划 3.4）：SQLite 存储 + FTS5 词法检索（CJK 二元组 + BM25 标题加权）
/// + 向量余弦（内存暴力，万条级够用）→ RRF 融合；候选区审核（candidate → approved/rejected）。
/// 词法双路：FTS 为主（相关度排序），LIKE 兜底（子串/未切出的关键词），全离线可用。
/// </summary>
public sealed class KnowledgeStore(string dbPath, KnowledgeConfig config, ModelRouter router)
{
    private const string ItemColumns = "id, content, source, source_ref, status, usage_count, created_at, title";
    private const string ItemColumnsJoined = "k.id, k.content, k.source, k.source_ref, k.status, k.usage_count, k.created_at, k.title";

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        // 并发配置（与 LogAgent.Memory.Database 一致）：WAL + busy_timeout，避免多入口并发写时 SQLITE_BUSY
        using var pragma = conn.CreateCommand();
        pragma.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA busy_timeout=5000;
            PRAGMA synchronous=NORMAL;
            """;
        pragma.ExecuteNonQuery();
        return conn;
    }

    private readonly ConcurrentDictionary<long, float[]> _vectorCache = new();

    public event EventHandler<string>? KnowledgeLog;

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
        using var conn = Open();

        // 1) 主表（新库直接带标题与检索列）
        Exec(conn, """
            CREATE TABLE IF NOT EXISTS knowledge_items (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                content TEXT NOT NULL,
                source TEXT NOT NULL DEFAULT 'manual',
                source_ref TEXT,
                status TEXT NOT NULL DEFAULT 'candidate',
                embedding BLOB,
                usage_count INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                title TEXT NOT NULL DEFAULT '',
                search_title TEXT NOT NULL DEFAULT '',
                search_body TEXT NOT NULL DEFAULT ''
            );
            """);

        // 2) 迁移：老库补列（title / search_title / search_body）
        var itemCols = QueryColumns(conn, "knowledge_items");
        foreach (var (name, ddl) in new[]
                 {
                     ("title", "TEXT NOT NULL DEFAULT ''"),
                     ("search_title", "TEXT NOT NULL DEFAULT ''"),
                     ("search_body", "TEXT NOT NULL DEFAULT ''"),
                 })
        {
            if (!itemCols.Contains(name))
            {
                Exec(conn, $"ALTER TABLE knowledge_items ADD COLUMN {name} {ddl}");
                Log($"知识库迁移：补充列 {name}");
            }
        }

        // 3) 迁移：老数据补算检索列
        BackfillSearchColumns(conn);

        // 4) 先卸旧触发器（旧版触发器引用旧 FTS 结构），再切换 FTS 表
        Exec(conn, """
            DROP TRIGGER IF EXISTS knowledge_ai;
            DROP TRIGGER IF EXISTS knowledge_ad;
            DROP TRIGGER IF EXISTS knowledge_au;
            """);
        var ftsCols = QueryColumns(conn, "knowledge_fts");
        if (ftsCols.Count > 0 && !ftsCols.Contains("search_body"))
        {
            Exec(conn, "DROP TABLE knowledge_fts;");
            ftsCols = [];
            Log("知识库迁移：重建 FTS 索引表");
        }
        if (ftsCols.Count == 0)
        {
            try
            {
                Exec(conn, """
                    CREATE VIRTUAL TABLE knowledge_fts USING fts5(
                        search_title, search_body, content='knowledge_items', content_rowid='id');
                    """);
            }
            catch (SqliteException ex)
            {
                // 精简版 SQLite（部分 Linux 发行版/Armbian 镜像的 e_sqlite3 未编译 FTS5）：
                // 不阻断启动，检索自动退化为 LIKE（SearchFts 会因表缺失抛异常并被 catch）。
                _ftsAvailable = false;
                Log($"FTS5 不可用，关键词检索降级为 LIKE：{ex.Message}");
                return;
            }
        }

        // 5) 触发器：同步 FTS（携带检索列）
        try
        {
            Exec(conn, """
                CREATE TRIGGER knowledge_ai AFTER INSERT ON knowledge_items BEGIN
                    INSERT INTO knowledge_fts(rowid, search_title, search_body)
                    VALUES (new.id, new.search_title, new.search_body);
                END;
                CREATE TRIGGER knowledge_ad AFTER DELETE ON knowledge_items BEGIN
                    INSERT INTO knowledge_fts(knowledge_fts, rowid, search_title, search_body)
                    VALUES ('delete', old.id, old.search_title, old.search_body);
                END;
                CREATE TRIGGER knowledge_au AFTER UPDATE OF search_title, search_body ON knowledge_items BEGIN
                    INSERT INTO knowledge_fts(knowledge_fts, rowid, search_title, search_body)
                    VALUES ('delete', old.id, old.search_title, old.search_body);
                    INSERT INTO knowledge_fts(rowid, search_title, search_body)
                    VALUES (new.id, new.search_title, new.search_body);
                END;
                """);
        }
        catch (SqliteException ex)
        {
            _ftsAvailable = false;
            Log($"FTS 触发器创建失败，关键词检索降级为 LIKE：{ex.Message}");
            return;
        }
        _ftsAvailable = true;

        // 6) 索引同步：行数不一致 → 全量重建（幂等，小库毫秒级）
        try
        {
            var baseCount = ScalarLong(conn, "SELECT COUNT(*) FROM knowledge_items");
            var ftsCount = ScalarLong(conn, "SELECT COUNT(*) FROM knowledge_fts");
            if (baseCount != ftsCount)
            {
                Exec(conn, "INSERT INTO knowledge_fts(knowledge_fts) VALUES('rebuild');");
                Log($"知识库索引重建：{baseCount} 条");
            }
        }
        catch (SqliteException ex)
        {
            _ftsAvailable = false;
            Log($"FTS 索引同步失败，关键词检索降级为 LIKE：{ex.Message}");
        }
    }

    /// <summary>FTS5 是否可用（精简镜像可能未编入；不可用时检索自动走 LIKE）。</summary>
    private bool _ftsAvailable = true;

    /// <summary>当前检索是否启用 FTS（doctor/UI 可读）。</summary>
    public bool IsFtsAvailable => _ftsAvailable;

    /// <summary>老数据补算检索列（迁移用；新库为空时零开销）。</summary>
    private void BackfillSearchColumns(SqliteConnection conn)
    {
        var rows = new List<(long Id, string Title, string Content)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id, title, content FROM knowledge_items WHERE search_body = ''";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
        }
        foreach (var (id, title, content) in rows)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE knowledge_items SET search_title = @st, search_body = @sb WHERE id = @id";
            cmd.Parameters.AddWithValue("@st", KnowledgeText.ToFtsText(title));
            cmd.Parameters.AddWithValue("@sb", KnowledgeText.ToFtsText(content));
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }
        if (rows.Count > 0) Log($"知识库迁移：补算检索列 {rows.Count} 条");
    }

    /// <summary>新增知识（默认候选；AutoApprove 或 source=manual/doc 直接转正）。生成 embedding 异步进行。</summary>
    public async Task<long> AddAsync(string content, string source, string? sourceRef = null,
        string? title = null, CancellationToken ct = default)
    {
        var ids = await AddRangeAsync([(title, content)], source, sourceRef, ct);
        return ids.Count > 0 ? ids[0] : -1;
    }

    /// <summary>批量新增（文档导入用；单连接写入，逐条异步向量化）。</summary>
    public async Task<List<long>> AddRangeAsync(IReadOnlyList<(string? Title, string Content)> entries,
        string source, string? sourceRef = null, CancellationToken ct = default)
    {
        var status = config.AutoApprove || source is "manual" or "doc" ? "approved" : "candidate";
        var ids = new List<long>();
        await using (var conn = Open())
        {
            foreach (var (title, content) in entries)
            {
                var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO knowledge_items (content, source, source_ref, status, created_at, title, search_title, search_body)
                    VALUES (@c, @s, @r, @st, @ts, @ti, @sti, @sb);
                    SELECT last_insert_rowid();
                    """;
                cmd.Parameters.AddWithValue("@c", content);
                cmd.Parameters.AddWithValue("@s", source);
                cmd.Parameters.AddWithValue("@r", (object?)sourceRef ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@st", status);
                cmd.Parameters.AddWithValue("@ts", DateTimeOffset.UtcNow.ToString("O"));
                cmd.Parameters.AddWithValue("@ti", title ?? "");
                cmd.Parameters.AddWithValue("@sti", KnowledgeText.ToFtsText(title ?? ""));
                cmd.Parameters.AddWithValue("@sb", KnowledgeText.ToFtsText(content));
                ids.Add(Convert.ToInt64(cmd.ExecuteScalar()));
            }
        }
        Log($"新增知识 {ids.Count} 条（{source}，{status}）");
        foreach (var (entry, id) in entries.Zip(ids))
            _ = Task.Run(() => TryEmbedAsync(id, entry.Content, ct), ct);
        return ids;
    }

    /// <summary>按来源删除（文档重复导入时替换旧块）。</summary>
    public int DeleteBySourceRef(string source, string sourceRef)
    {
        using var conn = Open();
        var ids = new List<long>();
        using (var query = conn.CreateCommand())
        {
            query.CommandText = "SELECT id FROM knowledge_items WHERE source = @s AND source_ref = @r";
            query.Parameters.AddWithValue("@s", source);
            query.Parameters.AddWithValue("@r", sourceRef);
            using var reader = query.ExecuteReader();
            while (reader.Read()) ids.Add(reader.GetInt64(0));
        }
        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM knowledge_items WHERE source = @s AND source_ref = @r";
        cmd.Parameters.AddWithValue("@s", source);
        cmd.Parameters.AddWithValue("@r", sourceRef);
        var deleted = cmd.ExecuteNonQuery();
        foreach (var id in ids) _vectorCache.TryRemove(id, out _);
        if (deleted > 0) Log($"替换旧知识：来源 {sourceRef} 删除 {deleted} 条");
        return deleted;
    }

    private async Task TryEmbedAsync(long id, string content, CancellationToken ct)
    {
        try
        {
            var vector = await router.EmbedAsync(content, ct);
            if (vector is null) return;
            _vectorCache[id] = vector;
            await using var conn = Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE knowledge_items SET embedding = @e WHERE id = @id";
            cmd.Parameters.AddWithValue("@e", ToBlob(vector));
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Log($"知识 #{id} 向量化失败（降级为关键词检索）：{ex.Message}");
        }
    }

    public void Review(long id, bool approve)
    {
        using var conn = Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE knowledge_items SET status = @st WHERE id = @id";
        cmd.Parameters.AddWithValue("@st", approve ? "approved" : "rejected");
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
        Log($"知识 #{id} 审核：{(approve ? "转正" : "拒绝")}");
    }

    public void Delete(long id)
    {
        using var conn = Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM knowledge_items WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
        _vectorCache.TryRemove(id, out _);
    }

    public List<KnowledgeItem> List(string? status = null, int limit = 200)
    {
        using var conn = Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = status is null
            ? $"SELECT {ItemColumns} FROM knowledge_items ORDER BY id DESC LIMIT @n"
            : $"SELECT {ItemColumns} FROM knowledge_items WHERE status = @st ORDER BY id DESC LIMIT @n";
        cmd.Parameters.AddWithValue("@st", (object?)status ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@n", limit);
        return ReadItems(cmd);
    }

    /// <summary>混合检索：FTS（二元组 + BM25 标题加权）＋ LIKE 兜底 ＋ 向量余弦，RRF 融合，仅 approved。</summary>
    public async Task<List<KnowledgeHit>> SearchAsync(string query, int topK, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var tokens = KnowledgeText.QueryTokens(query);
        var keywordHits = SearchLexical(tokens, topK * 3);
        var vecHits = await SearchVectorAsync(query, topK * 3, ct);

        // RRF 融合
        var scores = new Dictionary<long, (double Score, KnowledgeItem Item, string Via)>();
        Merge(scores, keywordHits, "kw");
        Merge(scores, vecHits, "vec");
        var top = scores.Values
            .OrderByDescending(v => v.Score)
            .Take(topK)
            .Select(v => new KnowledgeHit(v.Item, v.Score, v.Via))
            .ToList();
        TouchUsage(top);
        return top;
    }

    private static void Merge(Dictionary<long, (double, KnowledgeItem, string)> scores,
        List<KnowledgeItem> ranked, string via)
    {
        for (var rank = 0; rank < ranked.Count; rank++)
        {
            var item = ranked[rank];
            var add = 1.0 / (60 + rank);
            if (scores.TryGetValue(item.Id, out var existing))
                scores[item.Id] = (existing.Item1 + add, existing.Item2, $"{existing.Item3}+{via}");
            else
                scores[item.Id] = (add, item, via);
        }
    }

    /// <summary>词法检索：FTS 优先；不足时用 LIKE 兜底补足（子串与未切出的关键词）。</summary>
    private List<KnowledgeItem> SearchLexical(List<string> tokens, int limit)
    {
        var hits = SearchFts(tokens, limit);
        if (tokens.Count == 0 || hits.Count >= limit) return hits;
        var seen = hits.Select(h => h.Id).ToHashSet();
        foreach (var extra in SearchLike(tokens, limit))
            if (seen.Add(extra.Id)) hits.Add(extra);
        return hits;
    }

    /// <summary>FTS 检索：CJK 二元组 + ASCII 词元，BM25 排序（标题权重 10、正文 1）。</summary>
    private List<KnowledgeItem> SearchFts(List<string> tokens, int limit)
    {
        if (!_ftsAvailable) return [];
        var match = KnowledgeText.BuildMatchQuery(tokens);
        if (match is null) return [];
        try
        {
            using var conn = Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT {ItemColumnsJoined}
                FROM knowledge_fts
                JOIN knowledge_items k ON k.id = knowledge_fts.rowid
                WHERE knowledge_fts MATCH @q AND k.status = 'approved'
                ORDER BY bm25(knowledge_fts, 10.0, 1.0)
                LIMIT @n;
                """;
            cmd.Parameters.AddWithValue("@q", match);
            cmd.Parameters.AddWithValue("@n", limit);
            return ReadItems(cmd);
        }
        catch (SqliteException ex)
        {
            Log($"FTS 检索异常（转 LIKE 兜底）：{ex.Message}");
            return [];
        }
    }

    /// <summary>LIKE 兜底：多词元子串命中（万条级足够）。</summary>
    private List<KnowledgeItem> SearchLike(List<string> tokens, int limit)
    {
        if (tokens.Count == 0) return [];
        try
        {
            using var conn = Open();
            var cmd = conn.CreateCommand();
            var where = string.Join(" OR ", tokens.Select((_, i) => $"(content LIKE @t{i} OR title LIKE @t{i})"));
            cmd.CommandText = $"""
                SELECT {ItemColumns}
                FROM knowledge_items WHERE ({where}) AND status = 'approved' ORDER BY id DESC LIMIT @n;
                """;
            for (var i = 0; i < tokens.Count; i++)
                cmd.Parameters.AddWithValue($"@t{i}", $"%{tokens[i]}%");
            cmd.Parameters.AddWithValue("@n", limit);
            return ReadItems(cmd);
        }
        catch (SqliteException ex)
        {
            Log($"关键词检索异常（降级）：{ex.Message}");
            return [];
        }
    }

    private async Task<List<KnowledgeItem>> SearchVectorAsync(string query, int limit, CancellationToken ct)
    {
        var queryVector = await router.EmbedAsync(query, ct);
        if (queryVector is null) return [];
        var approved = List("approved", 5000);
        var ranked = new List<(double Score, KnowledgeItem Item)>();
        foreach (var item in approved)
        {
            var vector = _vectorCache.TryGetValue(item.Id, out var cached)
                ? cached
                : LoadVector(item.Id);
            if (vector is null) continue;
            _vectorCache[item.Id] = vector;
            ranked.Add((Cosine(queryVector, vector), item));
        }
        return ranked.Where(r => r.Score > 0.35)
            .OrderByDescending(r => r.Score)
            .Take(limit)
            .Select(r => r.Item)
            .ToList();
    }

    /// <summary>使用计数（知识质量反馈闭环：被检索即 +1，供 UI 与后续淘汰策略参考）。</summary>
    private void TouchUsage(List<KnowledgeHit> hits)
    {
        if (hits.Count == 0) return;
        try
        {
            using var conn = Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText =
                $"UPDATE knowledge_items SET usage_count = usage_count + 1 WHERE id IN ({string.Join(",", hits.Select(h => h.Item.Id))})";
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            Log($"使用计数更新失败：{ex.Message}");
        }
    }

    private float[]? LoadVector(long id)
    {
        using var conn = Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT embedding FROM knowledge_items WHERE id = @id AND embedding IS NOT NULL";
        cmd.Parameters.AddWithValue("@id", id);
        var blob = cmd.ExecuteScalar() as byte[];
        return blob is null ? null : FromBlob(blob);
    }

    private static List<KnowledgeItem> ReadItems(SqliteCommand cmd)
    {
        var items = new List<KnowledgeItem>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            items.Add(new KnowledgeItem(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4), reader.GetInt32(5), reader.GetString(6),
                reader.IsDBNull(7) ? "" : reader.GetString(7)));
        return items;
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long ScalarLong(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static List<string> QueryColumns(SqliteConnection conn, string table)
    {
        var cols = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        try
        {
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) cols.Add(reader.GetString(1));
        }
        catch (SqliteException)
        {
            // 表不存在等情况：返回空
        }
        return cols;
    }

    private static byte[] ToBlob(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] FromBlob(byte[] blob)
    {
        var vector = new float[blob.Length / sizeof(float)];
        Buffer.BlockCopy(blob, 0, vector, 0, blob.Length);
        return vector;
    }

    private static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        return na == 0 || nb == 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    private void Log(string line) => KnowledgeLog?.Invoke(this, line);
}
