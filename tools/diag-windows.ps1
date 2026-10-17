# Diagnose LogAgent windows at Win32 level: enumerate ALL top-level windows (incl. hidden),
# print state, and ping each window's message loop (WM_NULL + timeout) to detect a stuck UI thread.
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class W32Diag {
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
}
"@

$proc = Get-Process LogAgent.App -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { "NOT_RUNNING"; exit 1 }
$target = [uint32]$proc.Id
"target pid: $target (started $($proc.StartTime.ToString('HH:mm:ss')))"

$script:found = 0
$cb = [W32Diag+EnumWindowsProc]{
    param([IntPtr]$h, [IntPtr]$lp)
    $wpid = [uint32]0
    [W32Diag]::GetWindowThreadProcessId($h, [ref]$wpid) | Out-Null
    if ($wpid -eq $target) {
        $script:found++
        $title = New-Object System.Text.StringBuilder 256
        [W32Diag]::GetWindowTextW($h, $title, 256) | Out-Null
        $cls = New-Object System.Text.StringBuilder 256
        [W32Diag]::GetClassNameW($h, $cls, 256) | Out-Null
        $visible = [W32Diag]::IsWindowVisible($h)
        $iconic = [W32Diag]::IsIconic($h)
        $res = [IntPtr]::Zero
        # flag 2 = SMTO_ABORTIFHUNG, 2000ms timeout
        $ok = [W32Diag]::SendMessageTimeout($h, 0, [IntPtr]::Zero, [IntPtr]::Zero, 2, 2000, [ref]$res)
        "hwnd=$h class='$($cls.ToString())' title='$($title.ToString())' visible=$visible iconic=$iconic pingOk=$([bool]$ok)"
    }
    return $true
}
[W32Diag]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null
"total windows: $script:found"
