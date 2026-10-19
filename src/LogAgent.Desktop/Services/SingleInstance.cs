using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;

namespace LogAgent.Desktop.Services;

/// <summary>
/// 单实例互斥：Windows 用命名 Mutex，Unix 用 Unix domain socket 文件（无 UI 依赖）。
/// 第二次启动时向已有实例发「显示窗口」请求后自行退出。
/// </summary>
public static class SingleInstance
{
    private static readonly string SocketPath = Path.Combine(
        Path.GetTempPath(), $"logagent-desktop-{Environment.UserName}.sock");

    private static Mutex? _mutex;
    private static Socket? _listener;
    /// <summary>Windows 上「显示窗口」命名事件句柄（保持存活，退出时释放）。</summary>
    private static EventWaitHandle? _showEvent;

    /// <summary>尝试成为唯一实例。</summary>
    public static bool TryAcquire()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                if (File.Exists(SocketPath)) File.Delete(SocketPath);
                _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                _listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
                _listener.Listen(1);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        try
        {
            _mutex = new Mutex(initiallyOwned: true, "LogAgent.Desktop.SingleInstance", out var created);
            if (created) return true;
            _mutex.Dispose();
            _mutex = null;
            return false;
        }
        catch (Exception)
        {
            return true; // 无法判定时放行，避免把用户挡在门外
        }
    }

    /// <summary>释放单实例资源（应用退出时调用）。</summary>
    public static void Release()
    {
        try { _showEvent?.Dispose(); } catch (Exception) { }
        try { _mutex?.ReleaseMutex(); } catch (Exception) { }
        try { _mutex?.Dispose(); } catch (Exception) { }
        try { _listener?.Dispose(); } catch (Exception) { }
        try { if (File.Exists(SocketPath)) File.Delete(SocketPath); } catch (Exception) { }
        _showEvent = null;
        _mutex = null;
        _listener = null;
    }

    /// <summary>请求已有实例把窗口带到前台（尽力而为）。</summary>
    public static void RequestShow()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                using var wake = EventWaitHandle.OpenExisting("LogAgent.ShowWindow");
                wake.Set();
                return;
            }

            if (!File.Exists(SocketPath)) return;
            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            client.Connect(new UnixDomainSocketEndPoint(SocketPath));
            client.Send([0x01]);
        }
        catch (Exception)
        {
            // 已有实例尚未就绪：静默退出即可
        }
    }

    /// <summary>后台监听「显示窗口」请求。</summary>
    public static void ListenForShowRequest(Action onShow)
    {
        // Windows：命名事件（与 Mutex 同名的单实例语义配合）
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                // 注意：事件句柄必须比监听线程活得更久，不能 using（否则立即释放）
                var wake = new EventWaitHandle(false, EventResetMode.AutoReset, "LogAgent.ShowWindow");
                _showEvent = wake;
                var thread = new Thread(() =>
                {
                    while (true)
                    {
                        try
                        {
                            wake.WaitOne();
                            onShow();
                        }
                        catch (Exception)
                        {
                            return;
                        }
                    }
                })
                { IsBackground = true };
                thread.Start();
            }
            catch (Exception)
            {
                // 事件创建失败：任务栏激活仍可用
            }
            return;
        }

        if (_listener is null) return;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                Socket? client = null;
                try
                {
                    client = await _listener.AcceptAsync();
                    await client.ReceiveAsync(new byte[1]);
                    onShow();
                }
                catch (Exception)
                {
                    return;
                }
                finally
                {
                    client?.Dispose();
                }
            }
        });
    }
}
