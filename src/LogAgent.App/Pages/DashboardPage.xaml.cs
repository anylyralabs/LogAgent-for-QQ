using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LogAgent.App.Services;
using LogAgent.Memory;
using LogAgent.Shared.Configuration;

namespace LogAgent.App.Pages;

public sealed record StatRow(string Provider, string Model, long Ok, long Fail,
    long TotalTokens, double AvgLatencyMs);

public partial class DashboardPage : Page
{
    public DashboardPage()
    {
        InitializeComponent();
        Load();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Load();

    private void Load()
    {
        SubtitleText.Text = $"连接 · 会话 · 知识库 · 模型路由 全景　（更新于 {DateTime.Now:HH:mm:ss}）";
        LoadConnectionStates();
        LoadStats();
        LoadRouterMeter();
        ConfigPathText.Text = $"配置：{AppConfigStore.GetConfigPath()}";
        DbPathText.Text = $"数据目录：{Path.Combine(AppConfigStore.GetConfigDirectory(), "data")}";
    }

    // ---------- 连接状态总览 ----------

    private void LoadConnectionStates()
    {
        // 每次都从磁盘读最新配置（AppHost.Config 是启动快照，可能已过时）
        var liveConfig = AppConfigStore.Load();

        // 官方 Bot
        if (AppHost.OfficialBotRunning)
        {
            SetState(OfficialBotState, "已连接", OkBrush);
            OfficialBotSub.Text = $"AppId {liveConfig.OfficialBot.AppId}";
        }
        else
        {
            SetState(OfficialBotState, "未连接", MutedBrush);
            OfficialBotSub.Text = "在「官方 Bot」页扫码连接";
        }

        // OneBot 服务
        var obConfig = liveConfig.OneBot11;
        if (AppHost.OneBotServerStarted)
        {
            SetState(OneBotState, "运行中", OkBrush);
            OneBotSub.Text = $"http://{obConfig.Host}:{obConfig.Port}";
        }
        else
        {
            SetState(OneBotState, "未启动", MutedBrush);
            OneBotSub.Text = "在「OneBot 连接」页启动服务";
        }

        // NapCat 进程
        var napCatConfig = liveConfig.NapCat;
        if (string.IsNullOrWhiteSpace(napCatConfig.ScriptPath))
        {
            SetState(NapCatState, "未配置", MutedBrush);
            NapCatSub.Text = "在「OneBot 连接」页选择启动脚本";
        }
        else if (!File.Exists(napCatConfig.ScriptPath))
        {
            SetState(NapCatState, "脚本不存在", WarnBrush);
            NapCatSub.Text = napCatConfig.ScriptPath;
        }
        else
        {
            SetState(NapCatState, "已配置", OkBrush);
            NapCatSub.Text = $"脚本 {Path.GetFileName(napCatConfig.ScriptPath)}　WebUI :{napCatConfig.WebUiPort}";
        }
    }

    private Brush OkBrush => (Brush)FindResource("OkFill");
    private Brush WarnBrush => (Brush)FindResource("WarnFill");
    private Brush MutedBrush => (Brush)FindResource("TextSecondary");

    private static void SetState(TextBlock target, string text, Brush brush)
    {
        target.Text = text;
        target.Foreground = brush;
    }

    // ---------- 统计 ----------

    private void LoadStats()
    {
        try
        {
            var db = new Database(Path.Combine(AppConfigStore.GetConfigDirectory(), "data", "logagent.db"));
            db.EnsureCreated();
            var store = new SessionStore(db);
            var sessions = store.ListSessions(100000);
            SessionCountText.Text = sessions.Count.ToString();
            var active = sessions.Count(s => s.EndTime is null);
            SessionSub.Text = active > 0 ? $"{active} 个进行中" : "全部已切段";

            var turns = store.ListRecentTurns(100000);
            TurnCountText.Text = turns.Count.ToString();
            var today = DateTimeOffset.Now.Date;
            var todayCount = turns.Count(t => DateTimeOffset.TryParse(t.Ts, out var ts) && ts.ToLocalTime().Date == today);
            TurnSub.Text = $"今日 +{todayCount}";
        }
        catch (Exception ex)
        {
            SessionCountText.Text = "!";
            TurnCountText.Text = "!";
            SessionSub.Text = Truncate(ex.Message);
        }

        try
        {
            var kb = new Knowledge.KnowledgeStore(
                Path.Combine(AppConfigStore.GetConfigDirectory(), "data", "knowledge.db"),
                AppConfigStore.Load().Knowledge,
                AppHost.Router);
            kb.EnsureCreated();
            var approved = kb.List("approved").Count;
            var candidate = kb.List("candidate").Count;
            KnowledgeText.Text = approved.ToString();
            KnowledgeSub.Text = candidate > 0 ? $"{candidate} 条待审核" : "无待审核";
        }
        catch (Exception ex)
        {
            KnowledgeText.Text = "!";
            KnowledgeSub.Text = Truncate(ex.Message);
        }
    }

    private static string Truncate(string s) => s.Length > 40 ? s[..40] + "…" : s;

    // ---------- 模型路由计量 ----------

    private void LoadRouterMeter()
    {
        var stats = AppHost.Stats.Snapshot();
        StatsGrid.ItemsSource = stats
            .Select(s => new StatRow(s.Provider, s.Model, s.Ok, s.Fail,
                s.PromptTokens + s.CompletionTokens, s.AvgLatencyMs))
            .ToList();

        // 今日累计（数据库持久化，跨重启；盯免费额度用）
        try
        {
            var (calls, ok, fail, pt, ct) = AppHost.Usage.Today();
            TodayUsageText.Text = calls > 0
                ? $"今日累计：{calls} 次调用（成功 {ok} / 失败 {fail}）· tokens P {pt / 1000.0:F1}K + C {ct / 1000.0:F1}K"
                : "今日累计：暂无调用";
            ModelCallText.Text = calls.ToString();
            ModelCallSub.Text = ct + pt > 0 ? $"今日 · {(ct + pt) / 1000.0:F1}K tokens" : "今日暂无";
        }
        catch (Exception ex)
        {
            TodayUsageText.Text = $"今日累计读取失败：{Truncate(ex.Message)}";
        }

        if (stats.Count == 0)
        {
            StatsGrid.Visibility = Visibility.Collapsed;
            StatsEmptyHint.Visibility = Visibility.Visible;
            if (ModelCallText.Text == "-" || ModelCallText.Text == "0") ModelCallSub.Text = "尚无调用";
            return;
        }

        StatsGrid.Visibility = Visibility.Visible;
        StatsEmptyHint.Visibility = Visibility.Collapsed;
        if (ModelCallText.Text is "-" or "0")
        {
            var totalCalls = stats.Sum(s => s.Ok + s.Fail);
            var totalTokens = stats.Sum(s => s.PromptTokens + s.CompletionTokens);
            ModelCallText.Text = totalCalls.ToString();
            ModelCallSub.Text = $"{totalTokens / 1000.0:F1}K tokens";
        }
    }
}
