# Click the "copy-all" button on the Logs page (found by visible Name) and dump clipboard to a file.
# ASCII-only script on purpose (PS 5.1 reads BOM-less UTF-8 .ps1 as ANSI);
# the Chinese button label is composed from char codes.
# Usage: powershell -File copy-logs.ps1 <output-file>
param([string]$OutFile = "E:\logagent-for-qq\tools\logs-dump.txt")
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

. (Join-Path $PSScriptRoot 'wake-logagent.ps1')

$proc = Get-Process LogAgent.App -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $proc) { "APP_NOT_RUNNING"; exit 1 }

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
$wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)

# "copy all" = 0x590D 0x5236 0x5168 0x90E8  (FuZhiQuanBu)
$name = -join (@(0x590D, 0x5236, 0x5168, 0x90E8) | ForEach-Object { [char]$_ })

$nameCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
$btn = $null
for ($i = 0; $i -lt $wins.Count; $i++) {
    $btn = $wins.Item($i).FindFirst([System.Windows.Automation.TreeScope]::Descendants, $nameCond)
    if ($btn) { break }
}
if (-not $btn) { "BUTTON_NOT_FOUND"; exit 1 }

$inv = $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
$inv.Invoke()
Start-Sleep -Milliseconds 900

$text = Get-Clipboard -Raw
if (-not $text) { "CLIPBOARD_EMPTY"; exit 1 }
[System.IO.File]::WriteAllText($OutFile, $text, [System.Text.UTF8Encoding]::new($false))
"SAVED chars=$($text.Length) -> $OutFile"
