# Send a message on the LogAgent chat page via UI Automation (E2E test helper).
# ASCII-only script on purpose (PowerShell 5.1 reads BOM-less UTF-8 .ps1 as ANSI);
# Chinese default text is composed from char codes.
param([string]$Text)
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

# Wake the main window first (the app may be minimized to the system tray)
. (Join-Path $PSScriptRoot 'wake-logagent.ps1')

if (-not $Text) {
    # default text (Chinese composed from char codes): hello, introduce yourself
    $Text = "$([char]0x4F60)$([char]0x597D)$([char]0xFF0C)$([char]0x4ECB)$([char]0x7ECD)$([char]0x4E00)$([char]0x4E0B)$([char]0x4F60)$([char]0x81EA)$([char]0x5DF1)"
}

$proc = Get-Process LogAgent.App -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $proc) { "APP_NOT_RUNNING"; exit 1 }

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)

try {
    $wp = $win.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
    $wp.SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Normal)
    Start-Sleep -Milliseconds 800
} catch { }

function Find-ById($rootEl, $id) {
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return $rootEl.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}

$box = Find-ById $win "InputBox"
if (-not $box) { "INPUT_NOT_FOUND"; exit 1 }
$vp = $box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
$vp.SetValue($Text)
Start-Sleep -Milliseconds 500

$btn = Find-ById $win "SendButton"
if (-not $btn) { "SEND_BUTTON_NOT_FOUND"; exit 1 }
$ip = $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
$ip.Invoke()
"sent: $Text"
