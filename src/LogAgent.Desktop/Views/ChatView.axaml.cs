using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace LogAgent.Desktop.Views;

public partial class ChatView : UserControl
{
    public ChatView()
    {
        InitializeComponent();
        InputBox.KeyDown += OnInputKeyDown;
        Loaded += (_, _) =>
        {
            if (DataContext is ViewModels.ChatViewModel vm) vm.ScrollRequested += ScrollToEnd;
        };
        Unloaded += (_, _) =>
        {
            if (DataContext is ViewModels.ChatViewModel vm) vm.ScrollRequested -= ScrollToEnd;
        };
    }

    private void ScrollToEnd() => MessagesScroll.ScrollToEnd();

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        // Enter 发送，Shift+Enter 换行（输入法组词期间不拦截）
        if (e.Key != Key.Enter) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
        e.Handled = true;
        if (DataContext is ViewModels.ChatViewModel vm) vm.SendCommand.Execute(null);
    }

    private void QuickInput_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string text } && DataContext is ViewModels.ChatViewModel vm)
        {
            vm.FillInput(text);
            InputBox.Focus();
            InputBox.CaretIndex = InputBox.Text?.Length ?? 0;
        }
    }
}
