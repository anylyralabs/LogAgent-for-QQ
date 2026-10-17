# Select the first row of a WPF DataGrid (by AutomationId) via UIA SelectionItemPattern.
# ASCII-only script on purpose (PS 5.1 reads BOM-less UTF-8 .ps1 as ANSI).
param([string]$GridId = "ProvidersGrid")
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

# Wake the main window first (the app may be minimized to the system tray)
. (Join-Path $PSScriptRoot 'wake-logagent.ps1')

$proc = Get-Process LogAgent.App -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $proc) { "APP_NOT_RUNNING"; exit 1 }

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
try {
    $wp = $win.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
    $wp.SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Normal)
    Start-Sleep -Milliseconds 600
} catch { }

$gc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $GridId)
$grid = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $gc)
if (-not $grid) { "GRID_NOT_FOUND"; exit 1 }

$rowCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::DataItem)
$row = $grid.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $rowCond)
if (-not $row) { "ROW_NOT_FOUND"; exit 1 }
try {
    $sp = $row.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    $sp.Select()
    "row selected"
} catch {
    "SELECT_FAILED: $($_.Exception.Message)"
}
