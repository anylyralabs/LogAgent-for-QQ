using LogAgent.Core;
using LogAgent.Knowledge;
using LogAgent.Routing;
using LogAgent.Shared.Configuration;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LogAgent.Tests;

// ─────────────────────────── 文本切分（KnowledgeText） ───────────────────────────

public class KnowledgeTextTests
{
    [Fact]
    public void IndexTokens_Cjk_ProducesOverlappingBigrams()
    {
        var tokens = KnowledgeText.IndexTokens("内存不足");
        Assert.Contains("内存", tokens);
        Assert.Contains("存不", tokens);
        Assert.Contains("不足", tokens);
    }

    [Fact]
    public void IndexTokens_Ascii_SplitsLetterDigitBoundary()
    {
        var tokens = KnowledgeText.IndexTokens("EXIT137 SIGSEGV");
        Assert.Contains("exit137", tokens);
        Assert.Contains("exit", tokens);
        Assert.Contains("137", tokens);
        Assert.Contains("sigsegv", tokens);
    }

    [Fact]
    public void QueryTokens_RemovesStopWords_AndCapsCount()
    {
        var tokens = KnowledgeText.QueryTokens("nginx 502 怎么回事");
        Assert.Contains("nginx", tokens);
        Assert.Contains("502", tokens);
        Assert.DoesNotContain("怎么", tokens);
        Assert.DoesNotContain("回事", tokens);
    }

    [Fact]
    public void ClipAround_LongContent_KeepsHitWithinBudget()
    {
        var content = new string('x', 500) + "内存不足的关键结论" + new string('y', 3000);
        var clipped = KnowledgeText.ClipAround(content, ["内存"], 600);
        Assert.True(clipped.Length < 800, $"length={clipped.Length}");
        Assert.Contains("内存不足的关键结论", clipped);
    }

    [Fact]
    public void ClipAround_ShortContent_Unchanged()
    {
        const string content = "短内容：内存不足";
        Assert.Equal(content, KnowledgeText.ClipAround(content, ["内存"], 600));
    }
}

// ─────────────────────────── 文档切块（KnowledgeImporter.SplitChunks） ───────────────────────────

public class KnowledgeImporterChunkTests
{
    [Fact]
    public void SplitChunks_SplitsByH2Headings()
    {
        var text = "# 标题\n\n## 第一节\n\n内容 A\n\n## 第二节\n\n内容 B";
        var chunks = KnowledgeImporter.SplitChunks(text, "fallback");
        Assert.Equal(2, chunks.Count);
        Assert.Equal("第一节", chunks[0].Title);
        Assert.Contains("内容 A", chunks[0].Body);
        Assert.Equal("第二节", chunks[1].Title);
        Assert.Contains("内容 B", chunks[1].Body);
    }

    [Fact]
    public void SplitChunks_FenceBlockBecomesTitle()
    {
        var text = "========\nexit with 139 (SIGSEGV)\n========\n报错堆栈：\n游戏进程退出码为 139，段错误信号 SIGSEGV。";
        var chunks = KnowledgeImporter.SplitChunks(text, "fallback");
        var chunk = Assert.Single(chunks);
        Assert.Equal("exit with 139 (SIGSEGV)", chunk.Title);
        Assert.Contains("退出码为 139", chunk.Body);
    }

    [Fact]
    public void SplitChunks_NoHeading_SingleChunk()
    {
        var text = "这是一段没有任何小标题的说明文本，长度也超过最小阈值，应该整体成为一个块。";
        var chunks = KnowledgeImporter.SplitChunks(text, "文件名");
        var chunk = Assert.Single(chunks);
        Assert.Equal("文件名", chunk.Title);
    }

    [Fact]
    public void SplitChunks_ShortPreamble_Dropped_WhenSectionsExist()
    {
        var text = "# 标题\n\n文档更新日期：2026-08-22\n\n## 正文节\n\n这是一段足够长的正文内容，用来验证前言被丢弃而正文保留。";
        var chunks = KnowledgeImporter.SplitChunks(text, "fallback");
        var chunk = Assert.Single(chunks);
        Assert.Equal("正文节", chunk.Title);
    }

    [Fact]
    public void SplitChunks_OversizeSection_PacksByParagraphs()
    {
        var para = new string('长', 900);
        var text = $"# T\n\n## 大节\n\n{para}\n\n{para}\n\n{para}";
        var chunks = KnowledgeImporter.SplitChunks(text, "T");
        Assert.True(chunks.Count >= 2, $"count={chunks.Count}");
        Assert.All(chunks, c => Assert.True(c.Body.Length <= 1600, $"chunk too long: {c.Body.Length}"));
    }

    [Fact]
    public void SplitChunks_OversizeSectionWithH3_SplitsBySubheadings()
    {
        var body = string.Join("\n\n", Enumerable.Range(1, 5).Select(i => $"### 子节{i}\n\n{new string('词', 400)}"));
        var text = $"# T\n\n## 大节\n\n{body}";
        var chunks = KnowledgeImporter.SplitChunks(text, "T");
        Assert.True(chunks.Count >= 5, $"count={chunks.Count}");
        Assert.Contains(chunks, c => c.Title == "子节1");
    }

    [Fact]
    public void SplitChunks_StripsOrdinalPrefixFromHeadings()
    {
        var longText = new string('细', 1600);
        var text = $"# T\n\n## 一、体系概述\n\n概述内容若干。\n\n## 二、核心渲染器\n\n### 1. Krypton Wrapper（KW）\n\n{longText}\n\n### 2. Holy GL4ES\n\n{longText}";
        var chunks = KnowledgeImporter.SplitChunks(text, "T");
        Assert.Contains(chunks, c => c.Title == "体系概述");
        Assert.Contains(chunks, c => c.Title == "Krypton Wrapper（KW）");
    }
}

// ─────────────────────────── 导入管道集成（临时库） ───────────────────────────

public class KnowledgeImporterIntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"la-imp-{Guid.NewGuid():N}");

    public KnowledgeImporterIntegrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private KnowledgeStore MakeStore()
    {
        var store = new KnowledgeStore(
            Path.Combine(_dir, "kb.db"), new KnowledgeConfig(), new ModelRouter(new AppConfig { Providers = [], Rules = [] }));
        store.EnsureCreated();
        return store;
    }

    [Fact]
    public async Task Import_ThenReimport_IsIdempotent_AndApproved()
    {
        var store = MakeStore();
        var doc = Path.Combine(_dir, "doc.md");
        await File.WriteAllTextAsync(doc,
            "# 测试文档\n\n## 崩溃退出码 139\n\nexit with 139 (SIGSEGV)，段错误通常是原生库 Bug 或渲染器驱动问题。");

        var importer = new KnowledgeImporter(store);
        var first = await importer.ImportPathAsync(_dir);
        Assert.Equal(1, first.Files);
        Assert.True(first.Chunks >= 1, $"chunks={first.Chunks}");
        Assert.Equal(0, first.Problems);
        var countAfterFirst = store.List("approved").Count;

        var second = await importer.ImportPathAsync(_dir);
        Assert.Equal(countAfterFirst, store.List("approved").Count); // 替换而非重复
        Assert.Equal(first.Chunks, second.Chunks);
    }

    [Fact]
    public async Task Imported_Content_SearchableByChinese()
    {
        var store = MakeStore();
        var doc = Path.Combine(_dir, "a.md");
        await File.WriteAllTextAsync(doc,
            "# 日志速查\n\n## 内存不足（OOM）\n\njava.lang.OutOfMemoryError 出现时，先检查 -Xmx 与宿主机内存的比例。");

        await new KnowledgeImporter(store).ImportPathAsync(doc);

        var hits = await store.SearchAsync("内存不足怎么处理", 3);
        Assert.NotEmpty(hits);
        Assert.Contains(hits, h => h.Item.Source == "doc" && h.Item.Title.Contains("内存不足"));
    }
}

// ─────────────────────────── 检索通道（FTS 二元组 + 计数） ───────────────────────────

public class KnowledgeStoreFtsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"la-fts-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch (IOException) { }
    }

    private KnowledgeStore Make()
    {
        var store = new KnowledgeStore(
            _dbPath, new KnowledgeConfig(), new ModelRouter(new AppConfig { Providers = [], Rules = [] }));
        store.EnsureCreated();
        return store;
    }

    [Fact]
    public async Task Search_ChinesePhrase_MatchesBigrams()
    {
        var store = Make();
        await store.AddAsync("游戏进程退出码为 139，段错误信号 SIGSEGV，通常是原生库或渲染器驱动问题", "manual");
        var hits = await store.SearchAsync("退出 139 怎么解决", 5);
        Assert.NotEmpty(hits);
    }

    [Fact]
    public async Task Search_TitleMatch_RanksFirst()
    {
        var store = Make();
        var titled = await store.AddAsync("这条正文里也顺带提到了骁龙芯片的兼容性。", "manual", title: "骁龙芯片渲染器指南");
        await store.AddAsync("骁龙芯片 在这里只是正文里的顺带一提。", "manual");

        var hits = await store.SearchAsync("骁龙芯片", 5);
        Assert.NotEmpty(hits);
        Assert.Equal(titled, hits[0].Item.Id); // 标题命中（BM25 10:1）排前
    }

    [Fact]
    public async Task Search_IncrementsUsageCount()
    {
        var store = Make();
        var id = await store.AddAsync("Kafka 消费积压先查消费者组状态", "manual");
        Assert.Equal(0, store.List("approved").Single(i => i.Id == id).UsageCount);
        await store.SearchAsync("kafka 积压", 3);
        Assert.Equal(1, store.List("approved").Single(i => i.Id == id).UsageCount);
    }
}

// ─────────────────────────── 旧库迁移（M4 结构 → 新检索结构） ───────────────────────────

public class KnowledgeStoreMigrationTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"la-mig-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch (IOException) { }
    }

    [Fact]
    public async Task LegacySchema_MigratesWithColumnsAndFts()
    {
        // 旧结构（M4 版）：无 title/search_* 列，FTS 仅 content 列
        using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE knowledge_items (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    content TEXT NOT NULL,
                    source TEXT NOT NULL DEFAULT 'manual',
                    source_ref TEXT,
                    status TEXT NOT NULL DEFAULT 'candidate',
                    embedding BLOB,
                    usage_count INTEGER NOT NULL DEFAULT 0,
                    created_at TEXT NOT NULL
                );
                CREATE VIRTUAL TABLE knowledge_fts USING fts5(content, content='knowledge_items', content_rowid='id');
                INSERT INTO knowledge_items (content, source, status, created_at)
                VALUES ('Kafka 消费积压先查消费者组状态', 'manual', 'approved', '2026-01-01T00:00:00Z');
                """;
            cmd.ExecuteNonQuery();
        }

        var store = new KnowledgeStore(
            _dbPath, new KnowledgeConfig(), new ModelRouter(new AppConfig { Providers = [], Rules = [] }));
        store.EnsureCreated();

        Assert.Contains(store.List("approved"), i => i.Content.Contains("Kafka"));
        var hits = await store.SearchAsync("kafka 积压", 3);
        Assert.Single(hits);
    }
}

// ─────────────────────────── 日志分析 Agent：知识库案例注入 ───────────────────────────

public class LogAnalysisKnowledgeCaseTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"la-lac-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch (IOException) { }
    }

    [Fact]
    public async Task Analyze_WithKnowledge_InjectsCasesWithCitation()
    {
        var appConfig = new AppConfig
        {
            Providers = [new ProviderConfig { Id = "p1", Name = "P1", ChatModel = "m", Enabled = true }],
            Rules =
            [
                new RouterRule { TaskType = TaskType.LogAnalysis, ProviderChain = ["p1"] },
                new RouterRule { TaskType = TaskType.DeepReasoning, ProviderChain = ["p1"] },
            ],
        };
        var inputs = new List<string>();
        var router = new ModelRouter(appConfig, _ => new FakeClient("p1", true)
        {
            OnInput = inputs.Add,
            ReplySelector = input => input.Contains("技术评审") ? "PASS 结论有证据支撑" : "根因：db connect failed",
        });

        var store = new KnowledgeStore(_dbPath, new KnowledgeConfig(), router);
        store.EnsureCreated();
        await store.AddAsync(
            "数据库连接失败（db connect failed）：先检查连接池上限与数据库是否可达，再看 maxPoolSize 与超时配置。",
            "manual", title: "数据库连接失败排查");

        var agent = new LogAnalysisAgent(router, store, new KnowledgeConfig { Enabled = true });
        var report = await agent.AnalyzeAsync(
            "ERROR db connect failed at Db.java:1\n   at a.B(c)\n   at d.E(f)\n   at g.H(i)\n");

        Assert.Contains("日志分析报告", report);
        Assert.Equal(2, inputs.Count);
        Assert.Contains("知识库相似案例", inputs[0]); // 案例注入根因推断
        Assert.Contains("[K1]", inputs[0]);           // 引用编号
        Assert.Contains("知识库相似案例", inputs[1]); // 自检也能对照案例
    }
}
