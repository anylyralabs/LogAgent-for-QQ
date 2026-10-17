using System.Windows;
using System.Windows.Controls;

namespace LogAgent.App.Pages;

public partial class KnowledgePage : Page
{
    private readonly KnowledgeViewModel _vm = new();

    public KnowledgePage()
    {
        InitializeComponent();
        DataContext = _vm;
    }

    /// <summary>导入文件（多选）：.md/.txt/.log，走 KnowledgeImporter 管道（## 切块）。</summary>
    private async void ImportFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择知识文档",
            Multiselect = true,
            Filter = "知识文档 (*.md;*.txt;*.log)|*.md;*.txt;*.log|所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true) return;
        await _vm.ImportPathsAsync(dialog.FileNames);
    }

    /// <summary>导入文件夹（递归）：目录下所有 .md/.txt/.log。</summary>
    private async void ImportFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择知识文档目录（递归导入 .md / .txt / .log）",
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        await _vm.ImportPathsAsync([dialog.SelectedPath]);
    }
}
