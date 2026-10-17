using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

// UI 诊断工具（不属于解决方案）：
//   Screenshot <output.png>             从系统托盘唤醒 LogAgent → 截其窗口 → 拍完放回原状态（托盘）
//   Screenshot --state [秒数] [间隔秒]   Win32 真实状态采样（托盘态 = visible=False）
//   Screenshot --hide                    把主窗口收进系统托盘（信号事件，兜底 SW_MINIMIZE）
//   Screenshot --fullscreen <output.png> 全屏截图（诊断显示/合成层问题）
//   Screenshot --printwindow <output.png> PrintWindow 抓窗口自绘内容（诊断硬件合成/MPO 差异）

if (args.Length > 1 && args[0] == "--printwindow")
{
    var pwProc = System.Diagnostics.Process.GetProcessesByName("LogAgent.App").FirstOrDefault();
    if (pwProc is null) { Console.WriteLine("LogAgent not running"); return; }
    var pwHwnd = FindMainHwnd(pwProc.Id);
    if (pwHwnd == IntPtr.Zero) { Console.WriteLine("window not found"); return; }
    Native.GetWindowRect(pwHwnd, out var pr);
    var pwW = pr.Right - pr.Left;
    var pwH = pr.Bottom - pr.Top;
    using var pwBmp = new Bitmap(pwW, pwH);
    using (var gPw = Graphics.FromImage(pwBmp))
    {
        var hdc = gPw.GetHdc();
        var ok = Native.PrintWindow(pwHwnd, hdc, 2); // PW_RENDERFULLCONTENT
        gPw.ReleaseHdc(hdc);
        Console.WriteLine($"PrintWindow ok={ok} {pwW}x{pwH}");
    }
    pwBmp.Save(args[1], ImageFormat.Png);
    Console.WriteLine($"saved {args[1]}");
    return;
}

if (args.Length > 1 && args[0] == "--fullscreen")
{
    var bounds = Native.GetSystemMetrics(0);
    var height = Native.GetSystemMetrics(1);
    using var full = new Bitmap(bounds, height);
    using var gFull = Graphics.FromImage(full);
    gFull.CopyFromScreen(0, 0, 0, 0, new Size(bounds, height));
    full.Save(args[1], ImageFormat.Png);
    Console.WriteLine($"saved {args[1]} {bounds}x{height}");
    return;
}

if (args.Length > 0 && args[0] == "--state")
{
    var totalSeconds = args.Length > 1 ? int.Parse(args[1]) : 0;
    var intervalSeconds = args.Length > 2 ? Math.Max(1, int.Parse(args[2])) : 10;
    var deadline = DateTime.Now.AddSeconds(totalSeconds);
    while (true)
    {
        var p = System.Diagnostics.Process.GetProcessesByName("LogAgent.App").FirstOrDefault();
        if (p is null)
        {
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} NOT_RUNNING");
            return;
        }
        var hw = FindMainHwnd(p.Id);
        if (hw == IntPtr.Zero)
        {
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} pid={p.Id} NO_WINDOW");
        }
        else
        {
            var pl = new Native.WINDOWPLACEMENT { length = Marshal.SizeOf<Native.WINDOWPLACEMENT>() };
            Native.GetWindowPlacement(hw, ref pl);
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} pid={p.Id} visible={Native.IsWindowVisible(hw)} iconic={Native.IsIconic(hw)} showCmd={pl.showCmd}");
        }
        if (DateTime.Now >= deadline) break;
        Thread.Sleep(intervalSeconds * 1000);
    }
    return;
}

if (args.Length > 0 && args[0] == "--hide")
{
    if (Signal("LogAgent.HideToTray"))
    {
        Console.WriteLine("hide signal sent");
        return;
    }
    var p0 = System.Diagnostics.Process.GetProcessesByName("LogAgent.App").FirstOrDefault();
    if (p0 is null) { Console.WriteLine("LogAgent not running"); return; }
    var hw0 = FindMainHwnd(p0.Id);
    if (hw0 == IntPtr.Zero) { Console.WriteLine("window not found"); return; }
    Native.ShowWindow(hw0, 6); // SW_MINIMIZE
    Console.WriteLine("minimized via SW_MINIMIZE (fallback)");
    return;
}

// ---------- 截图模式 ----------
var output = args.Length > 0 ? args[0] : "shot.png";
var proc = System.Diagnostics.Process.GetProcessesByName("LogAgent.App").FirstOrDefault();
if (proc is null)
{
    Console.WriteLine("LogAgent not running");
    return;
}
var hwnd = FindMainHwnd(proc.Id);
if (hwnd == IntPtr.Zero)
{
    Console.WriteLine("LogAgent window not found");
    return;
}

var wasBackground = !Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd);

// 收在托盘（隐藏）时：信号应用自己恢复窗口（WPF 状态一致，不玩 Win32 强拉）
if (!Native.IsWindowVisible(hwnd))
{
    Signal("LogAgent.ShowWindow");
    Thread.Sleep(900);
    hwnd = FindMainHwnd(proc.Id);
    if (hwnd == IntPtr.Zero) { Console.WriteLine("window missing after wake"); return; }
}
if (Native.IsIconic(hwnd))
{
    Native.ShowWindow(hwnd, 9); // SW_RESTORE
    Thread.Sleep(400);
}

// 抢前台：AttachThreadInput 变通（外部进程直接 SetForegroundWindow 会被前台锁拦截）
var fg = Native.GetForegroundWindow();
if (fg != IntPtr.Zero && fg != hwnd)
{
    var fgThread = Native.GetWindowThreadProcessId(fg, out _);
    var myThread = Native.GetCurrentThreadId();
    Native.AttachThreadInput(fgThread, myThread, true);
    Native.SetForegroundWindow(hwnd);
    Native.BringWindowToTop(hwnd);
    Native.AttachThreadInput(fgThread, myThread, false);
}
Native.SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002); // HWND_TOP | NOSIZE | NOMOVE
Thread.Sleep(2500); // 等 DWM 与 WPF 完成渲染（页面重时可避免拍到黑屏/半透明帧）

Native.GetWindowRect(hwnd, out var r);
var w = r.Right - r.Left;
var h = r.Bottom - r.Top;
using var bmp = new Bitmap(w, h);
using var g = Graphics.FromImage(bmp);
g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(w, h));
bmp.Save(output, ImageFormat.Png);
Console.WriteLine($"saved {output} {w}x{h} (wasBackground={wasBackground})");

// 放回原状态：之前是后台（托盘/最小化）→ 重新收进托盘，避免把后台应用留在展开态
if (wasBackground)
{
    if (!Signal("LogAgent.HideToTray"))
        Native.ShowWindow(hwnd, 6); // 事件不可用：退化为最小化
}

// ---------- 辅助 ----------

static IntPtr FindMainHwnd(int pid)
{
    var result = IntPtr.Zero;
    var sb = new StringBuilder(512);
    Native.EnumWindows((hwnd, _) =>
    {
        Native.GetWindowThreadProcessId(hwnd, out var wpid);
        if (wpid != (uint)pid) return true;
        sb.Clear();
        Native.GetWindowText(hwnd, sb, sb.Capacity);
        if (sb.ToString().StartsWith("LogAgent", StringComparison.Ordinal))
        {
            result = hwnd;
            return false; // 停止枚举
        }
        return true;
    }, IntPtr.Zero);
    return result;
}

static bool Signal(string eventName)
{
    try
    {
        using var ev = EventWaitHandle.OpenExisting(eventName);
        ev.Set();
        return true;
    }
    catch (Exception)
    {
        return false;
    }
}

internal static class Native
{
    [DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    internal static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    internal static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    internal static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT placement);

    [DllImport("user32.dll")]
    internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    internal static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    internal static extern bool BringWindowToTop(IntPtr hWnd);

    internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }
}
