# One-click build: publish LogAgent (win-x64 single file) then compile the Inno Setup installer.
# Usage: powershell -ExecutionPolicy Bypass -File build-installer.ps1
# Note: requires Inno Setup 6/7 installed (ISCC.exe). The build machine had it removed on request;
#       reinstall from https://jrsoftware.org/isdl.php (or GitHub jrsoftware/issrc releases) before packaging.
$ErrorActionPreference = "Stop"
$root = "E:\logagent-for-qq"
$iscc = "C:\Program Files\Inno Setup 7\ISCC.exe"

if (-not (Test-Path $iscc)) {
    Write-Host "ISCC not found at $iscc - install Inno Setup 6/7 first." -ForegroundColor Red
    exit 1
}

Write-Host "[1/2] Publishing app..." -ForegroundColor Cyan
dotnet publish "$root\src\LogAgent.App\LogAgent.App.csproj" -c Release -r win-x64 `
    --self-contained -p:PublishSingleFile=true -o "$root\publish\win-x64"
if ($LASTEXITCODE -ne 0) { Write-Host "publish failed" -ForegroundColor Red; exit 1 }

Write-Host "[2/2] Compiling installer..." -ForegroundColor Cyan
& $iscc "$root\tools\installer.iss" | Select-Object -Last 3

Write-Host "Done. Setup package is in $root\publish\installer\" -ForegroundColor Green
