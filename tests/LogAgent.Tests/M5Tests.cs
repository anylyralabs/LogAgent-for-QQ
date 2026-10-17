using System.Text.Json;
using LogAgent.Memory;
using LogAgent.NapCat;
using LogAgent.Routing;
using LogAgent.Shared.Messaging;
using LogAgent.Training;
using Xunit;

namespace LogAgent.Tests;

public class RedactorTests
{
    [Theory]
    [InlineData("联系 13812345678 我", "<PHONE>")]
    [InlineData("服务器 192.168.1.100 挂了", "<IP>")]
    [InlineData("密钥 sk-abcdefghijklmnopqrst 存在泄漏", "<API-KEY>")]
    [InlineData("api_key=supersecret123 已配置", "<SECRET>")]
    [InlineData("访问 https://user:pass123@example.com/x", "<URL-CRED>")]
    [InlineData("邮箱 someone@example.com 通知", "<EMAIL>")]
    public void Apply_ReplacesSensitiveData(string input, string expectedToken)
    {
        var result = Redactor.Apply(input);
        Assert.Contains(expectedToken, result);
        Assert.DoesNotContain("13812345678", result);
        Assert.DoesNotContain("supersecret123", result);
        Assert.DoesNotContain("pass123@", result);
    }

    [Fact]
    public void Apply_KeepsNormalContent()
    {
        const string text = "ERROR Failed to connect to database after 3000ms timeout at DbPool.java:42";
        Assert.Equal(text, Redactor.Apply(text));
    }
}

public class TrainingExporterTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"la-train-{Guid.NewGuid():N}.db");

    private (Database Db, SessionStore Store) Seed()
    {
        var db = new Database(_dbPath);
        db.EnsureCreated();
        var store = new SessionStore(db);
        var sessionId = store.CreateSession("777", ChatType.Group);

        // 两轮"相同提问"（用户纠正重问场景）→ DPO 对素材
        store.RecordTurn(sessionId, "Nginx 502 怎么解决", "重启就好了", qualityTag: "corrected");
        store.RecordTurn(sessionId, "Nginx 502 怎么解决", "502 通常是上游超时，先查 upstream 日志", qualityTag: "adopted");
        store.RecordTurn(sessionId, "手机号 13812345678 发给我", "已记录 13812345678", qualityTag: "adopted");
        store.RecordTurn(sessionId, "空回复测试", "");
        return (db, store);
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch (IOException) { }
    }

    [Fact]
    public void Export_OpenAiChat_RedactsAndFilters()
    {
        var (_, store) = Seed();
        var outPath = Path.Combine(Path.GetTempPath(), $"la-exp-{Guid.NewGuid():N}.jsonl");
        var exporter = new TrainingExporter(store);
        var result = exporter.ExportAsync(outPath,
            new TrainingExporter.ExportOptions(ExportFormat.OpenAiChat, ["none", "adopted", "corrected"])).Result;

        Assert.Equal(3, result.Lines); // 空回复被跳过
        var lines = File.ReadAllLines(outPath);
        using var doc = JsonDocument.Parse(lines[2]); // 含手机号的那条
        Assert.Contains("<PHONE>", doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
        Assert.DoesNotContain("13812345678", lines[2]);
        // 审计文件已生成
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(outPath)!, "export_audit.jsonl")));
    }

    [Fact]
    public void Export_AlternateFormats_ProduceExpectedShapes()
    {
        var (_, store) = Seed();
        var exporter = new TrainingExporter(store);

        var sharegpt = Path.Combine(Path.GetTempPath(), $"la-sg-{Guid.NewGuid():N}.jsonl");
        exporter.ExportAsync(sharegpt, new TrainingExporter.ExportOptions(ExportFormat.ShareGpt, ["none", "adopted"])).Wait();
        using (var doc = JsonDocument.Parse(File.ReadLines(sharegpt).First()))
        {
            Assert.Equal("human", doc.RootElement.GetProperty("conversations")[0].GetProperty("from").GetString());
        }

        var alpaca = Path.Combine(Path.GetTempPath(), $"la-al-{Guid.NewGuid():N}.jsonl");
        exporter.ExportAsync(alpaca, new TrainingExporter.ExportOptions(ExportFormat.Alpaca, ["none", "adopted"])).Wait();
        using (var doc = JsonDocument.Parse(File.ReadLines(alpaca).First()))
        {
            Assert.True(doc.RootElement.TryGetProperty("instruction", out _));
        }
    }

    [Fact]
    public void Export_DpoPair_PairsRepeatedQuestions()
    {
        var (_, store) = Seed();
        var outPath = Path.Combine(Path.GetTempPath(), $"la-dpo-{Guid.NewGuid():N}.jsonl");
        var exporter = new TrainingExporter(store);
        var result = exporter.ExportAsync(outPath,
            new TrainingExporter.ExportOptions(ExportFormat.DpoPair, ["none", "adopted", "corrected"])).Result;

        Assert.Equal(1, result.Lines); // 只有一组重复提问
        using var doc = JsonDocument.Parse(File.ReadLines(outPath).First());
        Assert.Equal("重启就好了", doc.RootElement.GetProperty("rejected").GetString());
        Assert.Contains("上游超时", doc.RootElement.GetProperty("chosen").GetString());
    }

    [Fact]
    public void Export_QualityTagFilter_ExcludesOthers()
    {
        var (_, store) = Seed();
        var outPath = Path.Combine(Path.GetTempPath(), $"la-q-{Guid.NewGuid():N}.jsonl");
        var exporter = new TrainingExporter(store);
        var result = exporter.ExportAsync(outPath,
            new TrainingExporter.ExportOptions(ExportFormat.OpenAiChat, ["adopted"])).Result;
        Assert.Equal(2, result.Lines); // corrected 那条被排除
    }
}

public class NapCatConfigWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"la-napcat-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void EnsureReverseWs_CreatesTemplateConfig()
    {
        Directory.CreateDirectory(_dir);
        var files = NapCatConfigWriter.EnsureReverseWs(_dir, 6701, "mytoken");

        Assert.Single(files);
        var text = File.ReadAllText(files[0]);
        Assert.Contains("\"LogAgent\"", text);
        Assert.Contains("ws://127.0.0.1:6701/onebot/v11/ws", text);
        Assert.Contains("mytoken", text);
    }

    [Fact]
    public void EnsureReverseWs_PreservesExistingConfig_AndIsIdempotent()
    {
        var configDir = Path.Combine(_dir, "config");
        Directory.CreateDirectory(configDir);
        File.WriteAllText(Path.Combine(configDir, "onebot11_12345.json"), """
            {"network":{"websocketClients":[{"name":"Other","url":"ws://x"}],"httpServers":[{"port":3000}]},"musicSignUrl":"x"}
            """);

        NapCatConfigWriter.EnsureReverseWs(_dir, 6701, "");
        NapCatConfigWriter.EnsureReverseWs(_dir, 6701, ""); // 二次调用应幂等

        var text = File.ReadAllText(Path.Combine(configDir, "onebot11_12345.json"));
        using var doc = System.Text.Json.JsonDocument.Parse(text);
        var clients = doc.RootElement.GetProperty("network").GetProperty("websocketClients");
        Assert.Equal(2, clients.GetArrayLength()); // Other + LogAgent，不重复
        Assert.Equal("ws://x", clients[0].GetProperty("url").GetString());
        Assert.Equal("LogAgent", clients[1].GetProperty("name").GetString());
        // 其它字段保留
        Assert.Equal(3000, doc.RootElement.GetProperty("network").GetProperty("httpServers")[0].GetProperty("port").GetInt32());
        Assert.Equal("x", doc.RootElement.GetProperty("musicSignUrl").GetString());
    }

    [Fact]
    public void EnsureReverseWs_UpdatesAllAccountConfigs()
    {
        var configDir = Path.Combine(_dir, "config");
        Directory.CreateDirectory(configDir);
        File.WriteAllText(Path.Combine(configDir, "onebot11_111.json"), "{}");
        File.WriteAllText(Path.Combine(configDir, "onebot11_222.json"), "{}");

        var files = NapCatConfigWriter.EnsureReverseWs(_dir, 6701, "");
        Assert.Equal(3, files.Count); // 模板 + 两个账号
    }

    [Fact]
    public void EnsureReverseWs_MissingDirectory_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            NapCatConfigWriter.EnsureReverseWs(Path.Combine(_dir, "nope"), 6701, ""));
    }
}

public class LocalChatHistoryTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"la-turns-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch (IOException) { }
    }

    [Fact]
    public void ListTurnsByChat_FiltersByChat_AndReturnsOldestFirst()
    {
        var db = new Database(_dbPath);
        db.EnsureCreated();
        using (var conn = db.Open())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO sessions (id, chat_id, chat_type, start_time) VALUES
                    ('s1', 'local-console', 'Private', '2026-10-15T10:00:00+08:00'),
                    ('s2', 'group:123', 'Group', '2026-10-15T10:00:00+08:00');
                INSERT INTO turns (session_id, user_input, agent_reply, quality_tag, model_used, ts) VALUES
                    ('s1', '你好', '你好呀', 'none', NULL, '2026-10-15T10:00:01+08:00'),
                    ('s2', '群消息', '群回复', 'none', NULL, '2026-10-15T10:00:02+08:00'),
                    ('s1', '再见', '拜拜', 'none', NULL, '2026-10-15T10:00:03+08:00');
                """;
            cmd.ExecuteNonQuery();
        }

        var store = new SessionStore(db);
        var turns = store.ListTurnsByChat("local-console", 10);

        Assert.Equal(2, turns.Count);
        Assert.Equal("你好", turns[0].UserInput); // 旧→新
        Assert.Equal("再见", turns[1].UserInput);
        Assert.All(turns, t => Assert.Equal("local-console", t.ChatId));
    }

    [Fact]
    public void ListTurnsByChat_RespectsLimit_KeepsLatest()
    {
        var db = new Database(_dbPath);
        db.EnsureCreated();
        using (var conn = db.Open())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO sessions (id, chat_id, chat_type, start_time) VALUES
                    ('s1', 'local-console', 'Private', 'x');
                INSERT INTO turns (session_id, user_input, agent_reply, quality_tag, model_used, ts) VALUES
                    ('s1', 'q1', 'a1', 'none', NULL, 'x'),
                    ('s1', 'q2', 'a2', 'none', NULL, 'x'),
                    ('s1', 'q3', 'a3', 'none', NULL, 'x');
                """;
            cmd.ExecuteNonQuery();
        }

        var store = new SessionStore(db);
        var turns = store.ListTurnsByChat("local-console", 2);

        Assert.Equal(2, turns.Count);
        Assert.Equal("q2", turns[0].UserInput); // 取最近 2 条，旧→新
        Assert.Equal("q3", turns[1].UserInput);
    }
}

public class OpenAiModelsParseTests
{
    [Fact]
    public void ParseModelIds_SortsDedupsAndSkipsBadEntries()
    {
        var ids = OpenAiCompatClient.ParseModelIds("""
            {"object":"list","data":[
                {"id":"deepseek-reasoner","object":"model"},
                {"id":"deepseek-chat","object":"model"},
                {"id":"deepseek-chat","object":"model"},
                {"no_id":true},
                {"id":123},
                {"id":""},
                null
            ]}
            """);
        Assert.Equal(new[] { "deepseek-chat", "deepseek-reasoner" }, ids);
    }

    [Fact]
    public void ParseModelIds_MissingOrEmptyData_ReturnsEmpty()
    {
        Assert.Empty(OpenAiCompatClient.ParseModelIds("""{"object":"list","data":[]}"""));
        Assert.Empty(OpenAiCompatClient.ParseModelIds("{}"));
    }

    [Fact]
    public void ParseModels_ReadsContextAndReasoningMetadata()
    {
        var models = OpenAiCompatClient.ParseModels("""
            {"object":"list","data":[
                {"id":"hy4-preview","name":"Hy4 preview","context_length":960000,"max_output_tokens":64000,
                 "supports_reasoning":true,"only_reasoning":true},
                {"id":"plain-model","name":"Plain"}
            ]}
            """);
        Assert.Equal(2, models.Count);
        var hy4 = models.First(m => m.Id == "hy4-preview");
        Assert.Equal(960000, hy4.ContextTokens);
        Assert.Equal(64000, hy4.MaxOutputTokens);
        Assert.True(hy4.Reasoning);
        Assert.Equal("Hy4 preview", hy4.Name);
        var plain = models.First(m => m.Id == "plain-model");
        Assert.Equal(0, plain.ContextTokens);
        Assert.False(plain.Reasoning);
    }
}
