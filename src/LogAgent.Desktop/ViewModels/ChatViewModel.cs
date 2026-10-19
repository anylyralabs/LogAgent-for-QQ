using System.Collections.ObjectModel;
using Avalonia.Threading;
using LogAgent.Core;

namespace LogAgent.Desktop.ViewModels;

/// <summary>本机对话页：与 QQ 消息共用同一条 AgentRuntime 管线；历史从会话库回显。</summary>
public sealed class ChatViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private string _input = "";
    private bool _busy;

    public ChatViewModel(ShellViewModel shell)
    {
        _shell = shell;
        SendCommand = new RelayCommand(SendAsync, () => !_busy && Input.Trim().Length > 0);
        NewTopicCommand = new RelayCommand(NewTopicAsync, () => !_busy);
        ClearCommand = new RelayCommand(Clear);
        _shell.LocalReplied += OnReply;
        LoadHistory();
    }

    public ObservableCollection<ChatMessage> Messages { get; } = [];

    public string Input
    {
        get => _input;
        set
        {
            if (Set(ref _input, value)) SendCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (Set(ref _busy, value)) SendCommand.RaiseCanExecuteChanged();
        }
    }

    public RelayCommand SendCommand { get; }
    public RelayCommand NewTopicCommand { get; }
    public RelayCommand ClearCommand { get; }

    /// <summary>新消息到达时请求视图滚动到底部。</summary>
    public event Action? ScrollRequested;

    private void LoadHistory()
    {
        try
        {
            var turns = _shell.Host.Sessions?.Store.ListTurnsByChat(AgentHost.LocalChatId, 20) ?? [];
            if (turns.Count == 0)
            {
                Messages.Add(ChatMessage.Note("本机对话控制台 —— 与 QQ 消息走同一条处理管线，发一条试试"));
                return;
            }

            Messages.Add(ChatMessage.Note($"已载入最近 {turns.Count} 轮历史（来自会话库）"));
            foreach (var t in turns)
            {
                Messages.Add(ChatMessage.User(t.UserInput));
                Messages.Add(new ChatMessage(ChatRole.Agent, t.AgentReply, DateTime.Now));
            }
        }
        catch (Exception)
        {
            // 历史载入失败不阻断对话
        }
    }

    private async Task SendAsync()
    {
        var text = Input.Trim();
        if (text.Length == 0 || IsBusy) return;
        Input = "";
        Messages.Add(ChatMessage.User(text));
        RequestScroll();
        IsBusy = true;
        try
        {
            await _shell.SendLocalChatAsync(text);
        }
        catch (Exception ex)
        {
            Messages.Add(ChatMessage.Note($"处理失败：{ex.Message}"));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task NewTopicAsync()
    {
        Messages.Add(ChatMessage.Note("已开启新话题"));
        try
        {
            var sessions = _shell.Host.Sessions;
            if (sessions is not null) await sessions.CutAsync(AgentHost.LocalChatId, "manual");
        }
        catch (Exception ex)
        {
            Messages.Add(ChatMessage.Note($"切段失败：{ex.Message}"));
        }
        RequestScroll();
    }

    private void OnReply(ChatMessage message) => Dispatcher.UIThread.Post(() =>
    {
        Messages.Add(message);
        RequestScroll();
    });

    private void Clear() => Messages.Clear();

    private void RequestScroll() => ScrollRequested?.Invoke();

    /// <summary>快捷输入（按钮 Tag 传文本）。</summary>
    public void FillInput(string text) => Input = text;
}
