# UI smoke test: launch exe, walk all navigation pages via UI Automation,
# check the process stays alive after each page renders. ASCII-only on purpose
# (Windows PowerShell 5.1 reads BOM-less .ps1 as ANSI and mangles CJK literals).
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$exe = "E:\logagent-for-qq\publish\win-x64\LogAgent.App.exe"
# Kill stale instances first so only one window exists (multiple instances make UIA pick the wrong one)
Get-Process LogAgent.App -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1
$proc = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 7

if ($proc.HasExited) { "CRASHED_ON_START exit=$($proc.ExitCode)"; exit 1 }

# The app starts minimized to the system tray: wake it first so UIA can enumerate items
. (Join-Path $PSScriptRoot 'wake-logagent.ps1')

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
if (-not $win) { "WINDOW_NOT_FOUND"; exit 1 }

# Normalize the window: while minimized, ListBox virtualization realizes only a few items
try {
    $wp = $win.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
    $wp.SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Normal)
    Start-Sleep -Milliseconds 1500
} catch { "normalize failed: $($_.Exception.Message)" }

$liCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
$items = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $liCond)
"Found $($items.Count) nav items"

for ($i = 0; $i -lt $items.Count; $i++) {
    if ($proc.HasExited) { "item[$i] => CRASHED(exit=$($proc.ExitCode))"; break }
    $item = $items.Item($i)
    $name = $item.Current.Name
    try {
        $sel = $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        $sel.Select()
        Start-Sleep -Milliseconds 1800
        if ($proc.HasExited) { "item[$i] '$name' => CRASHED_AFTER_SELECT(exit=$($proc.ExitCode))"; break }
        # Check for an error dialog (global fallback dialog = page render failure)
        $wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
        if ($wins.Count -gt 1) {
            $dialogName = $wins.Item(1).Current.Name
            "item[$i] '$name' => ERROR_DIALOG: $dialogName"
            break
        }
        "item[$i] => OK"
    } catch {
        "item[$i] => SELECT_FAILED: $($_.Exception.Message)"
    }
}
if (-not $proc.HasExited) { "--- ALL PAGES RENDERED, PROCESS ALIVE ---" }
