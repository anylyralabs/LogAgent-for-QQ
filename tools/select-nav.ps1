# Select a nav item by index in the running LogAgent window (for screenshot workflows).
# Usage: powershell -File select-nav.ps1 <index>
param([int]$Index = 0)
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

# Wake the main window first (the app may be minimized to the system tray)
. (Join-Path $PSScriptRoot 'wake-logagent.ps1')

$proc = Get-Process LogAgent.App -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $proc) { "APP_NOT_RUNNING"; exit 1 }

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)

# Normalize the window: while minimized, ListBox virtualization realizes only a few items
try {
    $wp = $win.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
    $wp.SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Normal)
    Start-Sleep -Milliseconds 1500
} catch { "normalize failed: $($_.Exception.Message)" }

$liCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
$items = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $liCond)
if ($Index -ge $items.Count) { "INDEX_OUT_OF_RANGE ($($items.Count) items)"; exit 1 }

$sel = $items.Item($Index).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
$sel.Select()
Start-Sleep -Milliseconds 1500
"selected index=$Index"
