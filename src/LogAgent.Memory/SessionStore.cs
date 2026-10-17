using System.Text.Json;
using LogAgent.Shared.Messaging;
using Microsoft.Data.Sqlite;

namespace LogAgent.Memory;

/// <summary>会话存取：创建会话、追加原文、记录问答轮次（切断/压缩在 M2 落地，这里先铺数据层）。</summary>
public sealed class SessionStore(Database db)
{
    public string CreateSession(string chatId, ChatType chatType, string? cutReason = null)
    {
        var id = Guid.NewGuid().ToString("N");
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sessions (id, chat_id, chat_type, start_time, cut_reason)
            VALUES (@id, @chat, @type, @start, @cut);
            """;
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@chat", chatId);
        cmd.Parameters.AddWithValue("@type", chatType.ToString());
        cmd.Parameters.AddWithValue("@start", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@cut", (object?)cutReason ?? DBNull.Value);
        cmd.ExecuteNonQuery();
        return id;
    }

    public void AppendRawMessage(string sessionId, BotMessage message)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO raw_messages (session_id, message_id, sender_id, sender_name, content, ts)
            VALUES (@s, @mid, @sender, @name, @content, @ts);
            """;
        cmd.Parameters.AddWithValue("@s", sessionId);
        cmd.Parameters.AddWithValue("@mid", message.MessageId);
        cmd.Parameters.AddWithValue("@sender", message.SenderId);
        cmd.Parameters.AddWithValue("@name", message.SenderName);
        cmd.Parameters.AddWithValue("@content", message.Text);
        cmd.Parameters.AddWithValue("@ts", message.Timestamp.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public void RecordTurn(string sessionId, string userInput, string agentReply,
        string? retrievedCtx = null, string? modelUsed = null, string qualityTag = "none")
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO turns (session_id, user_input, retrieved_ctx, agent_reply, quality_tag, model_used, ts)
            VALUES (@s, @input, @ctx, @reply, @tag, @model, @ts);
            """;
        cmd.Parameters.AddWithValue("@s", sessionId);
        cmd.Parameters.AddWithValue("@input", userInput);
        cmd.Parameters.AddWithValue("@ctx", (object?)retrievedCtx ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@reply", agentReply);
        cmd.Parameters.AddWithValue("@tag", qualityTag);
        cmd.Parameters.AddWithValue("@model", (object?)modelUsed ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ts", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public int CountSessions()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sessions";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>关闭会话（写结束时间/切断原因/L1 摘要）。</summary>
    public void CloseSession(string sessionId, DateTimeOffset endTime, string? cutReason, string? summary)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE sessions SET end_time = @end, cut_reason = @cut, summary = @sum WHERE id = @id;
            """;
        cmd.Parameters.AddWithValue("@end", endTime.ToString("O"));
        cmd.Parameters.AddWithValue("@cut", (object?)cutReason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sum", (object?)summary ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@id", sessionId);
        cmd.ExecuteNonQuery();
    }

    public void InsertSummary(string sessionId, int level, string content)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO summaries (session_id, level, content, created_at)
            VALUES (@s, @level, @content, @ts);
            """;
        cmd.Parameters.AddWithValue("@s", sessionId);
        cmd.Parameters.AddWithValue("@level", level);
        cmd.Parameters.AddWithValue("@content", content);
        cmd.Parameters.AddWithValue("@ts", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>某 chat 最近 N 条 L1 摘要（新→旧）。</summary>
    public List<string> GetRecentSummaries(string chatId, int count = 3)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT summary FROM sessions
            WHERE chat_id = @chat AND summary IS NOT NULL AND summary != ''
            ORDER BY start_time DESC LIMIT @n;
            """;
        cmd.Parameters.AddWithValue("@chat", chatId);
        cmd.Parameters.AddWithValue("@n", count);
        var result = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    public int CountMessages(string sessionId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM raw_messages WHERE session_id = @s";
        cmd.Parameters.AddWithValue("@s", sessionId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public sealed record TurnRow(long Id, string ChatId, string SessionId, string UserInput, string AgentReply,
        string QualityTag, string? ModelUsed, string Ts);

    /// <summary>最近问答对（记忆浏览器/训练导出数据源），新→旧。</summary>
    public List<TurnRow> ListRecentTurns(int limit = 100)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT t.id, s.chat_id, t.session_id, t.user_input, t.agent_reply, t.quality_tag, t.model_used, t.ts
            FROM turns t JOIN sessions s ON s.id = t.session_id
            ORDER BY t.id DESC LIMIT @n;
            """;
        cmd.Parameters.AddWithValue("@n", limit);
        var rows = new List<TurnRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows.Add(new TurnRow(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetString(7)));
        return rows;
    }

    /// <summary>指定 chat 的最近问答对（本机对话页回显历史），旧→新。</summary>
    public List<TurnRow> ListTurnsByChat(string chatId, int limit = 30)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT t.id, s.chat_id, t.session_id, t.user_input, t.agent_reply, t.quality_tag, t.model_used, t.ts
            FROM turns t JOIN sessions s ON s.id = t.session_id
            WHERE s.chat_id = @c
            ORDER BY t.id DESC LIMIT @n;
            """;
        cmd.Parameters.AddWithValue("@c", chatId);
        cmd.Parameters.AddWithValue("@n", limit);
        var rows = new List<TurnRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows.Add(new TurnRow(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetString(7)));
        rows.Reverse();
        return rows;
    }

    public sealed record SessionRow(string Id, string ChatId, string ChatType, string StartTime, string? EndTime, string? CutReason, string? Summary, int MessageCount);

    public List<SessionRow> ListSessions(int limit = 300)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.id, s.chat_id, s.chat_type, s.start_time, s.end_time, s.cut_reason, s.summary,
                   (SELECT COUNT(*) FROM raw_messages m WHERE m.session_id = s.id) AS msg_count
            FROM sessions s ORDER BY s.start_time DESC LIMIT @n;
            """;
        cmd.Parameters.AddWithValue("@n", limit);
        var rows = new List<SessionRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new SessionRow(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                Convert.ToInt32(reader.GetInt64(7))));
        }
        return rows;
    }
}
