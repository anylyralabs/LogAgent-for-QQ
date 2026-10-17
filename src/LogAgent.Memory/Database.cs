using Microsoft.Data.Sqlite;

namespace LogAgent.Memory;

/// <summary>SQLite 存储：连接工厂 + schema v1（会话/原文/轮次/摘要，规划 3.3 与 3.8）。</summary>
public sealed class Database(string dbPath)
{
    public string DbPath { get; } = dbPath;

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={DbPath}");
        conn.Open();
        ApplyConcurrencyPragmas(conn);
        return conn;
    }

    /// <summary>
    /// 并发配置（多群消息并发写 + 官方Bot/本机对话同时在线）：
    /// WAL = 读写不互斥、写锁粒度小；busy_timeout = 写冲突时等待而非立即 SQLITE_BUSY；
    /// NORMAL = WAL 下的推荐同步级别（兼顾安全与性能）。
    /// </summary>
    internal static void ApplyConcurrencyPragmas(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA busy_timeout=5000;
            PRAGMA synchronous=NORMAL;
            """;
        cmd.ExecuteNonQuery();
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(DbPath))!);
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS sessions (
                id           TEXT PRIMARY KEY,
                chat_id      TEXT NOT NULL,
                chat_type    TEXT NOT NULL,
                start_time   TEXT NOT NULL,
                end_time     TEXT,
                cut_reason   TEXT,
                summary      TEXT,
                token_stats  INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS raw_messages (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id   TEXT NOT NULL REFERENCES sessions(id),
                message_id   TEXT NOT NULL,
                sender_id    TEXT,
                sender_name  TEXT,
                content      TEXT NOT NULL,
                ts           TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_raw_messages_session ON raw_messages(session_id);
            CREATE TABLE IF NOT EXISTS turns (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id      TEXT NOT NULL REFERENCES sessions(id),
                user_input      TEXT NOT NULL,
                retrieved_ctx   TEXT,
                agent_reply     TEXT,
                trace_summary   TEXT,
                quality_tag     TEXT NOT NULL DEFAULT 'none',
                model_used      TEXT,
                ts              TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_turns_session ON turns(session_id);
            CREATE TABLE IF NOT EXISTS summaries (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id   TEXT NOT NULL REFERENCES sessions(id),
                level        INTEGER NOT NULL,
                content      TEXT NOT NULL,
                created_at   TEXT NOT NULL
            );
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
}
