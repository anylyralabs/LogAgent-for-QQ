using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace LogAgent.App.Themes;

/// <summary>
/// Win11 窗口材质（Mica/圆角）实验代码——当前未启用。
/// 实测本机环境 DWM 材质不可用（API 返回成功但无视觉效果，透明底会呈黑色），
/// 已切换为纯色 Fluent 方案（Fluent.xaml / WindowBackground）；本文件留作 M6 材质回归参考。
/// </summary>
public static class MicaHelper
{
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;   // Win11 22H2+
    private const int DwmwaMicaEffect = 1029;         // Win11 21H2 旧属性
    private const int DwmsbtMainWindow = 2;           // Mica
    private const int DwmwcpRound = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>应用 Mica。返回 true=材质生效（窗口保持透明底）；false=已降级纯色。</summary>
    public static bool Apply(Window window, Color fallbackColor)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) || !TryEnableBackdrop(window))
        {
            window.Background = new SolidColorBrush(fallbackColor);
            return false;
        }
        return true;
    }

    private static bool TryEnableBackdrop(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return false;

            var round = DwmwcpRound;
            DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref round, sizeof(int));

            var backdrop = DwmsbtMainWindow;
            var hr = DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref backdrop, sizeof(int));
            if (hr != 0)
            {
                var mica = 1;
                hr = DwmSetWindowAttribute(hwnd, DwmwaMicaEffect, ref mica, sizeof(int));
            }
            if (hr != 0) return false;

            // 让 WPF 告诉 DWM：客户区透明，由系统绘制材质
            if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } target)
                target.BackgroundColor = Colors.Transparent;
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }
}
