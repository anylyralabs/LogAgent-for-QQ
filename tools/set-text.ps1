# Set text of a WPF control (TextBox / editable ComboBox) by AutomationId via ValuePattern.
# Searches all top-level windows of the LogAgent process (main window and dialogs).
# Usage: powershell -File set-text.ps1 <automationId> <text>
param([string]$AutomationId, [string]$Text)
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

# Wake the main window first (the app may be minimized to the system tray)
. (Join-Path $PSScriptRoot 'wake-logagent.ps1')

$proc = Get-Process LogAgent.App -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $proc) { "APP_NOT_RUNNING"; exit 1 }

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
$wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)

$idCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $AutomationId)
$ctrl = $null
for ($i = 0; $i -lt $wins.Count; $i++) {
    $ctrl = $wins.Item($i).FindFirst([System.Windows.Automation.TreeScope]::Descendants, $idCond)
    if ($ctrl) { break }
}
if (-not $ctrl) { "CONTROL_NOT_FOUND: $AutomationId"; exit 1 }

$vp = $ctrl.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
$vp.SetValue($Text)
"text set: $AutomationId"
