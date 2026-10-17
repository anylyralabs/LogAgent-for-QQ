# Report LogAgent window state via UI Automation. Diagnostic only (does NOT wake the app).
# If the app is in the system tray (hidden), reports IN_TRAY.
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$proc = Get-Process LogAgent.App -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { "NOT_RUNNING"; exit 1 }

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
$wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
if ($wins.Count -eq 0) { "pid=$($proc.Id) IN_TRAY (hidden, no visible top-level window)"; exit 0 }

for ($i = 0; $i -lt $wins.Count; $i++) {
    $w = $wins.Item($i)
    try {
        $wp = $w.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
        "pid=$($proc.Id) win[$i] name='$($w.Current.Name)' state=$($wp.Current.WindowVisualState)"
    } catch {
        "pid=$($proc.Id) win[$i] name='$($w.Current.Name)' (no WindowPattern)"
    }
}
