# Send PageDown/PageUp keys to the focused chat list (WPF: focus list -> keys).
param([string]$Key = "PGDN", [int]$Times = 5)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms

$proc = Get-Process LogAgent.App -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $proc) { "APP_NOT_RUNNING"; exit 1 }

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
if (-not $win) { "WINDOW_NOT_FOUND"; exit 1 }

$listCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::List)
$lists = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $listCond)
if ($lists.Count -eq 0) { "LIST_NOT_FOUND"; exit 1 }
$list = $lists[$lists.Count - 1]
$list.SetFocus()
Start-Sleep -Milliseconds 300
for ($i = 0; $i -lt $Times; $i++) {
    [System.Windows.Forms.SendKeys]::SendWait("{$Key}")
    Start-Sleep -Milliseconds 160
}
"sent $Times x $Key"
