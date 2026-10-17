using System.Threading;
using System.Windows;

namespace LogAgent.App;

/// <summary>
/// 应用入口：全局异常兜底 + 单实例。
/// 单实例策略：已有实例在运行时（可能收在系统托盘），第二次启动改为唤醒其主窗口后退出——绝不双开。
/// </summary>
public partial class App : Application
{
    private static Mutex? _instanceMutex;

    public App()
    {
        // 后台工具型 UI：默认强制软件渲染。硬件加速（D3D/合成层）在部分驱动状态
        // （独显驱动重置、Basic Display Adapter 回退、远程会话切换等）下会出现整窗黑屏
        // 且无法自愈——本应用界面很简单，软件渲染完全够用，确定性优先。
        // 如需恢复硬件加速：设置环境变量 LOGAGENT_HW_RENDER=1。
        if (Environment.GetEnvironmentVariable("LOGAGENT_HW_RENDER") != "1")
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        // 全局异常兜底：GUI 崩溃时弹详细错误（含 InnerException 链），不再无声闪退
        DispatcherUnhandledException += (_, e) =>
        {
            MessageBox.Show(BuildErrorText(e.Exception), "LogAgent 出错了",
                MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
    }

    private static string BuildErrorText(Exception ex)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("未处理异常：");
        sb.AppendLine();
        var current = ex;
        for (var depth = 0; current is not null && depth < 5; depth++)
        {
            sb.AppendLine($"[{depth}] {current.GetType().Name}: {current.Message}");
            current = current.InnerException;
        }
        sb.AppendLine();
        sb.AppendLine(ex.StackTrace);
        return sb.ToString();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new Mutex(true, "LogAgent.SingleInstance", out var isFirstInstance);
        if (!isFirstInstance)
        {
            // 已有实例：唤醒它的主窗口（从托盘弹出），本实例直接退出
            try
            {
                using var wake = EventWaitHandle.OpenExisting("LogAgent.ShowWindow");
                wake.Set();
            }
            catch (Exception)
            {
                // 唤醒事件尚未就绪：静默退出（不双开）
            }
            Shutdown();
            return;
        }

        base.OnStartup(e);
        new MainWindow().Show(); // 手动创建主窗口（不用 StartupUri，便于单实例路径下不创建窗口）
    }
}
