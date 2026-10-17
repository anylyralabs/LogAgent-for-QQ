# List all top-level windows of the LogAgent process with name + visual state (diagnostic).
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$proc = Get-Process LogAgent.App -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { "NOT_RUNNING"; exit 1 }

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
$wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
"process pid=$($proc.Id), top-level windows: $($wins.Count)"
for ($i = 0; $i -lt $wins.Count; $i++) {
    $w = $wins.Item($i)
    try {
        $wp = $w.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
        "win[$i] name='$($w.Current.Name)' class='$($w.Current.ClassName)' state=$($wp.Current.WindowVisualState) offscreen=$($w.Current.IsOffscreen)"
    } catch {
        "win[$i] name='$($w.Current.Name)' class='$($w.Current.ClassName)' (no WindowPattern)"
    }
}
