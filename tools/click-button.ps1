# Click a WPF control by its x:Name (AutomationId) in the running LogAgent window(s).
# Supports Button (InvokePattern) and expandable controls like ComboBox (ExpandCollapsePattern).
# Usage: powershell -File click-button.ps1 <automationId>
param([string]$AutomationId)
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

if (-not $ctrl) {
    "CONTROL_NOT_FOUND: $AutomationId"
    # diagnostics: list buttons of the first window
    $btnCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
    $buttons = $wins.Item(0).FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond)
    "Buttons on window[0]: $($buttons.Count)"
    for ($i = 0; $i -lt [Math]::Min($buttons.Count, 25); $i++) {
        $b = $buttons.Item($i)
        "  [$i] id='$($b.Current.AutomationId)' name='$($b.Current.Name)'"
    }
    exit 1
}

try {
    $invoke = $ctrl.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $invoke.Invoke()
    Start-Sleep -Milliseconds 2500
    "clicked $AutomationId"
} catch {
    try {
        $ec = $ctrl.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
        $ec.Expand()
        Start-Sleep -Milliseconds 800
        "expanded $AutomationId"
    } catch {
        "NO_PATTERN: $AutomationId"
        exit 1
    }
}
