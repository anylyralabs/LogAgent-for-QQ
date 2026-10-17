using System.Drawing;
using WinForms = System.Windows.Forms;

namespace LogAgent.App.Services;

/// <summary>
/// 系统托盘（QQ 式后台驻留）：通知区图标 + 右键菜单（打开主界面 / 退出）。
/// 由 MainWindow 在「最小化 / 开机自启」时配合 Hide() 使用；双击或菜单恢复主界面。
/// </summary>
public sealed class TrayManager : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private bool _hintShown;

    public TrayManager(Action onOpen, Action onExit)
    {
        _icon = new WinForms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "LogAgent — QQ 日志分析助手",
            Visible = true,
        };

        var menu = new WinForms.ContextMenuStrip();
        var openItem = new WinForms.ToolStripMenuItem("打开主界面")
        {
            Font = new Font(WinForms.SystemInformation.MenuFont, FontStyle.Bold),
        };
        openItem.Click += (_, _) => onOpen();
        var exitItem = new WinForms.ToolStripMenuItem("退出 LogAgent");
        exitItem.Click += (_, _) => onExit();
        menu.Items.Add(openItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => onOpen();
    }

    /// <summary>首次收进托盘时气泡提示一次（避免每次最小化都弹）。</summary>
    public void ShowFirstHint()
    {
        if (_hintShown) return;
        _hintShown = true;
        try
        {
            _icon.BalloonTipTitle = "LogAgent 正在后台运行";
            _icon.BalloonTipText = "双击托盘图标可重新打开主界面；右键可退出。";
            _icon.ShowBalloonTip(3000);
        }
        catch (Exception)
        {
            // 系统通知不可用时忽略
        }
    }

    /// <summary>优先取 exe 自带图标（单文件发布同样有效），失败退回系统默认图标。</summary>
    private static Icon LoadIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var icon = Icon.ExtractAssociatedIcon(exe);
                if (icon is not null) return icon;
            }
        }
        catch (Exception) { }
        return SystemIcons.Application;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
