using System.Collections.ObjectModel;
using System.IO;
using LogAgent.App.Infrastructure;
using LogAgent.Memory;
using LogAgent.Shared.Configuration;

namespace LogAgent.App.Pages;

public sealed class TurnItem(SessionStore.TurnRow row) : ObservableObject
{
    public string ChatId { get; } = row.ChatId;
    public string Time { get; } = DateTimeOffset.Parse(row.Ts).ToLocalTime().ToString("MM-dd HH:mm");
    public string Question { get; } = row.UserInput.Length > 80 ? row.UserInput[..80] + "…" : row.UserInput;
    public string Answer { get; } = row.AgentReply.Length > 120 ? row.AgentReply[..120] + "…" : row.AgentReply;
    public string Quality { get; } = row.QualityTag;
    public string Model { get; } = row.ModelUsed ?? "";
}

public sealed class MemoryViewModel : ObservableObject
{
    public ObservableCollection<TurnItem> Turns { get; } = [];

    public RelayCommand RefreshCommand { get; }

    public MemoryViewModel()
    {
        RefreshCommand = new RelayCommand(Load);
        Load();
    }

    private void Load()
    {
        Turns.Clear();
        try
        {
            var db = new Database(Path.Combine(AppConfigStore.GetDataDirectory(), "logagent.db"));
            var store = new SessionStore(db);
            foreach (var turn in store.ListRecentTurns(100))
                Turns.Add(new TurnItem(turn));
        }
        catch (Exception) { /* 服务未初始化时库可能不存在 */ }
    }
}
