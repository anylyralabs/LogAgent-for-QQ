# Test: click a button by AutomationId, detect whether a NEW top-level window (dialog) appears, then close it with Esc.
# Usage: powershell -File test-dialog.ps1 <automationId>
param([string]$AutomationId)
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

# Wake the main window first (the app may be minimized to the system tray)
. (Join-Path $PSScriptRoot 'wake-logagent.ps1')

$proc = Get-Process LogAgent.App -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $proc) { "APP_NOT_RUNNING"; exit 1 }

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
$before = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond).Count

$idCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $AutomationId)
$ctrl = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $idCond)
if (-not $ctrl) { "CONTROL_NOT_FOUND: $AutomationId"; exit 1 }

$ctrl.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Seconds 2

$after = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond).Count
"windows before=$before after=$after"
if ($after -gt $before) {
    "DIALOG_APPEARED"
    # close the dialog with Esc via the dialog itself
    $wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
    for ($i = 0; $i -lt $wins.Count; $i++) {
        $w = $wins.Item($i)
        if ($w.Current.NativeWindowHandle -ne $proc.MainWindowHandle) {
            $w.SetFocus()
            Start-Sleep -Milliseconds 300
        }
    }
} else {
    "NO_DIALOG"
}
