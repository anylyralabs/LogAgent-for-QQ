using System.Collections.ObjectModel;
using System.IO;
using LogAgent.App.Infrastructure;
using LogAgent.App.Services;
using LogAgent.Knowledge;
using LogAgent.Shared.Configuration;

namespace LogAgent.App.Pages;

public sealed class KnowledgeItemVM(KnowledgeItem item) : ObservableObject
{
    public long Id { get; } = item.Id;
    public string Source { get; } = item.Source;
    public string Status { get; } = item.Status;
    public string Title { get; } = item.Title;
    public string CreatedAt { get; } = DateTimeOffset.Parse(item.CreatedAt).ToLocalTime().ToString("MM-dd HH:mm");

    public string SourceDisplay => Source switch
    {
        "web" => "网页学习",
        "user_reply" => "群友告知",
        "manual" => "手动录入",
        "doc" => "文档导入",
        _ => Source,
    };

    /// <summary>列表展示：标题 + 内容（标题来自 ## 切块，便于辨认文档条目）。</summary>
    public string Display => Title.Length == 0 ? Content : $"【{Title}】{Content}";

    public string StatusDisplay => Status switch
    {
        "candidate" => "待审核",
        "approved" => "已生效",
        "rejected" => "已拒绝",
        _ => Status,
    };

    private string _content = item.Content;
    public string Content { get => _content; set => Set(ref _content, value); }
}

public sealed class KnowledgeViewModel : ObservableObject
{
    public ObservableCollection<KnowledgeItemVM> Items { get; } = [];

    public string StatusFilter
    {
        get => _statusFilter;
        set { if (Set(ref _statusFilter, value)) Load(); }
    }

    public KnowledgeItemVM? Selected
    {
        get => _selected;
        set => Set(ref _selected, value);
    }

    public string NewContent { get => _newContent; set => Set(ref _newContent, value); }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand ApproveCommand { get; }
    public RelayCommand RejectCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand AddManualCommand { get; }

    private string _statusFilter = "candidate";
    private KnowledgeItemVM? _selected;
    private string _newContent = "";

    public KnowledgeViewModel()
    {
        RefreshCommand = new RelayCommand(Load);
        ApproveCommand = new RelayCommand(() => Review(true), () => Selected is not null);
        RejectCommand = new RelayCommand(() => Review(false), () => Selected is not null);
        DeleteCommand = new RelayCommand(DeleteSelected, () => Selected is not null);
        AddManualCommand = new RelayCommand(AddManual, () => NewContent.Trim().Length > 3);
        Load();
    }

    private static KnowledgeStore? Open()
    {
        var store = AppHost.Knowledge;
        if (store is not null) return store;
        try
        {
            var s = new KnowledgeStore(
                Path.Combine(AppConfigStore.GetConfigDirectory(), "data", "knowledge.db"),
                AppConfigStore.Load().Knowledge,
                AppHost.Router);
            s.EnsureCreated();
            return s;
        }
        catch { return null; }
    }

    private void Load()
    {
        var store = Open();
        if (store is null) return;
        Items.Clear();
        foreach (var item in store.List(string.IsNullOrEmpty(StatusFilter) ? null : StatusFilter))
            Items.Add(new KnowledgeItemVM(item));

        // 审核概览计数
        PendingCount = store.List("candidate").Count;
        ApprovedCount = store.List("approved").Count;
        RejectedCount = store.List("rejected").Count;
    }

    public int PendingCount
    {
        get => _pendingCount;
        private set => Set(ref _pendingCount, value);
    }

    public int ApprovedCount
    {
        get => _approvedCount;
        private set => Set(ref _approvedCount, value);
    }

    public int RejectedCount
    {
        get => _rejectedCount;
        private set => Set(ref _rejectedCount, value);
    }

    private int _pendingCount;
    private int _approvedCount;
    private int _rejectedCount;

    private void Review(bool approve)
    {
        if (Selected is not { } selected) return;
        Open()?.Review(selected.Id, approve);
        Load();
    }

    private void DeleteSelected()
    {
        if (Selected is not { } selected) return;
        Open()?.Delete(selected.Id);
        Load();
    }

    private void AddManual()
    {
        var store = Open();
        if (store is null) return;
        _ = store.AddAsync(NewContent.Trim(), "manual");
        NewContent = "";
        Raise(nameof(NewContent));
        System.Threading.Tasks.Task.Delay(200).ContinueWith(_ => System.Windows.Application.Current?.Dispatcher.Invoke(Load));
    }

    // ---------- 文档导入（与无头命令 import-kb 共用 KnowledgeImporter 管道） ----------

    public string ImportStatus
    {
        get => _importStatus;
        private set => Set(ref _importStatus, value);
    }

    public bool Importing
    {
        get => _importing;
        private set => Set(ref _importing, value);
    }

    private string _importStatus = "";
    private bool _importing;

    public async Task ImportPathsAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        var store = Open();
        if (store is null)
        {
            ImportStatus = "知识库未就绪";
            return;
        }
        Importing = true;
        var files = 0;
        var chunks = 0;
        var problems = 0;
        try
        {
            var importer = new KnowledgeImporter(store);
            foreach (var path in paths)
            {
                ImportStatus = $"导入中…（{Path.GetFileName(path.TrimEnd('\\', '/'))}）";
                var result = await importer.ImportPathAsync(path);
                files += result.Files;
                chunks += result.Chunks;
                problems += result.Problems;
            }
            ImportStatus = problems == 0
                ? $"✅ 已导入 {files} 个文件 / {chunks} 条知识"
                : $"✅ 导入 {files} 个文件 / {chunks} 条知识（{problems} 个文件失败）";
            StatusFilter = "approved"; // 导入的文档块直接生效，切到已生效便于查看
        }
        catch (Exception ex)
        {
            ImportStatus = $"❌ 导入失败：{ex.Message}";
        }
        finally
        {
            Importing = false;
            Load();
        }
    }
}
