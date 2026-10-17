using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using LogAgent.App.Infrastructure;
using LogAgent.App.Services;

namespace LogAgent.App.Pages;

public enum ChatRole { User, Agent, Note }

/// <summary>对话页消息条目。</summary>
public sealed class ChatMessage
{
    public required ChatRole Role { get; init; }
    public required string Text { get; init; }
    public string Time { get; init; } = "";

    public static ChatMessage User(string text)
        => new() { Role = ChatRole.User, Text = text, Time = DateTime.Now.ToString("HH:mm") };

    public static ChatMessage Agent(string text)
        => new() { Role = ChatRole.Agent, Text = text, Time = DateTime.Now.ToString("HH:mm") };

    public static ChatMessage Note(string text) => new() { Role = ChatRole.Note, Text = text };
}

/// <summary>按角色选择气泡模板（用户 / 机器人 / 系统提示）。</summary>
public sealed class ChatMessageTemplateSelector : DataTemplateSelector
{
    public DataTemplate? UserTemplate { get; set; }
    public DataTemplate? AgentTemplate { get; set; }
    public DataTemplate? NoteTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) => item switch
    {
        ChatMessage { Role: ChatRole.User } => UserTemplate,
        ChatMessage { Role: ChatRole.Agent } => AgentTemplate,
        _ => NoteTemplate,
    };
}

/// <summary>
/// 对话页 ViewModel：跨页面导航保持同一实例（历史不丢）；消息经 AppHost 走与 QQ 相同的管线，
/// 回复通过 AppHost.LocalChatReply 事件回流。启动时从会话库回显 local-console 的历史问答。
/// </summary>
public sealed class ChatViewModel : ObservableObject
{
    private static ChatViewModel? _shared;

    public static ChatViewModel Shared => _shared ??= new ChatViewModel();

    public ObservableCollection<ChatMessage> Messages { get; } = [];

    private string _inputText = "";
    public string InputText { get => _inputText; set => Set(ref _inputText, value); }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }

    public bool HasMessages => Messages.Count > 0;

    public RelayCommand SendCommand { get; }
    public RelayCommand NewTopicCommand { get; }
    public RelayCommand ClearCommand { get; }

    /// <summary>有新消息时请求页面滚动到底部。</summary>
    public event Action? ScrollRequested;

    private ChatViewModel()
    {
        SendCommand = new RelayCommand(() => _ = SendAsync(null));
        NewTopicCommand = new RelayCommand(() => _ = SendAsync("新话题"));
        ClearCommand = new RelayCommand(ClearDisplay);
        AppHost.LocalChatReply += OnAgentReply;
        LoadHistory();
    }

    /// <summary>从会话库回显本机对话历史（跨重启保留）。</summary>
    private void LoadHistory()
    {
        try
        {
            AppHost.EnsureLocalChat();
            var turns = AppHost.Sessions?.Store.ListTurnsByChat(AppHost.LocalChatId, 20) ?? [];
            if (turns.Count == 0)
            {
                Append(ChatMessage.Note("本机对话控制台 —— 消息与 QQ 走同一条处理管线，发一条试试吧"));
                return;
            }
            Append(ChatMessage.Note($"已载入本机对话历史（最近 {turns.Count} 轮，来自会话库）"));
            foreach (var t in turns)
            {
                Append(new ChatMessage { Role = ChatRole.User, Text = t.UserInput, Time = ShortTime(t.Ts) });
                Append(new ChatMessage { Role = ChatRole.Agent, Text = t.AgentReply, Time = ShortTime(t.Ts) });
            }
        }
        catch
        {
            // 历史载入失败（首次运行/库未就绪）不阻断对话
        }
    }

    private async Task SendAsync(string? preset)
    {
        var text = (preset ?? InputText).Trim();
        if (text.Length == 0 || IsBusy) return;
        if (preset is null) InputText = "";

        Append(ChatMessage.User(text));
        IsBusy = true;
        try
        {
            await AppHost.SendLocalChatAsync(text);
        }
        catch (Exception ex)
        {
            Append(ChatMessage.Note($"处理失败：{ex.Message}"));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnAgentReply(string text)
    {
        var app = System.Windows.Application.Current;
        if (app is null) return;
        app.Dispatcher.Invoke(() => Append(ChatMessage.Agent(text)));
    }

    private void ClearDisplay()
    {
        Messages.Clear();
        Append(ChatMessage.Note("显示已清空（已保存的会话记录不受影响）"));
    }

    private void Append(ChatMessage message)
    {
        Messages.Add(message);
        Raise(nameof(HasMessages));
        ScrollRequested?.Invoke();
    }

    private static string ShortTime(string ts)
        => DateTimeOffset.TryParse(ts, out var t) ? t.ToLocalTime().ToString("MM-dd HH:mm") : ts;
}
