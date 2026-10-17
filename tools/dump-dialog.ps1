# List all descendant elements of a window (diagnostic).
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, [int]$args[0])
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
if (-not $win) { "WINDOW_NOT_FOUND"; exit 1 }
"TITLE: $($win.Current.Name)"

$all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
"descendants: $($all.Count)"
for ($i = 0; $i -lt $all.Count; $i++) {
    $e = $all.Item($i)
    $t = $e.Current.Name
    if ($t.Length -gt 400) { $t = $t.Substring(0, 400) + "...(truncated)" }
    "[$i] $($e.Current.ControlType.ProgrammaticName) '$t'"
}
