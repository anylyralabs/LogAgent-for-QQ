# Render diagnostic: show a trivial WPF window (white bg + red text) for 25s.
# If this renders black too, the machine-wide WPF/D3D path is broken (not LogAgent's bug).
Add-Type -AssemblyName PresentationFramework

$win = New-Object System.Windows.Window
$win.Title = "WPF Render Test"
$win.Width = 460
$win.Height = 260
$win.Left = 120
$win.Top = 120
$win.Topmost = $true
$win.Background = [System.Windows.Media.Brushes]::White

$tb = New-Object System.Windows.Controls.TextBlock
$tb.Text = "WPF RENDER TEST OK`n(white window + red text => WPF renders fine)"
$tb.FontSize = 18
$tb.Foreground = [System.Windows.Media.Brushes]::Red
$tb.Margin = "20"
$win.Content = $tb

$win.Show()
Start-Sleep -Seconds 25
$win.Close()
Write-Output "closed"
