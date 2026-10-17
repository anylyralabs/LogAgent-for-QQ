# Wake the LogAgent main window from the system tray (signal the app's named event).
# No-op when the app is not running. Dot-source from other scripts:
#   . (Join-Path $PSScriptRoot 'wake-logagent.ps1')
try {
    $ev = [System.Threading.EventWaitHandle]::OpenExisting("LogAgent.ShowWindow")
    $ev.Set() | Out-Null
    $ev.Close()
    Start-Sleep -Milliseconds 900
} catch { }
