# Poll all top-level windows (name/class/state) of LogAgent over time.
param([int]$Samples = 3, [int]$IntervalSec = 30)
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

for ($i = 1; $i -le $Samples; $i++) {
    $proc = Get-Process LogAgent.App -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $proc) { "[$i] NOT_RUNNING"; break }
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
    $wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
    $line = "[$i] pid=$($proc.Id) windows=$($wins.Count):"
    for ($j = 0; $j -lt $wins.Count; $j++) {
        $w = $wins.Item($j)
        try {
            $wp = $w.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
            $line += " ['$($w.Current.Name)' / $($w.Current.ClassName) / $($wp.Current.WindowVisualState)]"
        } catch {
            $line += " ['$($w.Current.Name)' / no-state]"
        }
    }
    $line
    if ($i -lt $Samples) { Start-Sleep -Seconds $IntervalSec }
}
