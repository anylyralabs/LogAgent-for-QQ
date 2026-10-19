namespace LogAgent.Desktop.ViewModels;

/// <summary>对话气泡角色。</summary>
public enum ChatRole { User, Agent, Note }

/// <summary>对话条目（气泡 / 系统提示）。</summary>
public sealed record ChatMessage(ChatRole Role, string Text, DateTime Time)
{
    public string TimeText => Time.ToString("HH:mm");
    public bool IsUser => Role == ChatRole.User;
    public bool IsAgent => Role == ChatRole.Agent;
    public bool IsNote => Role == ChatRole.Note;

    public static ChatMessage Note(string text) => new(ChatRole.Note, text, DateTime.Now);
    public static ChatMessage User(string text) => new(ChatRole.User, text, DateTime.Now);
}
