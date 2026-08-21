# Install Ink Container as the Windows screensaver
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$scr = Join-Path $root "dist\InkContainer\InkContainer.scr"
if (-not (Test-Path $scr)) {
    & (Join-Path $root "build.ps1")
}
$full = (Resolve-Path $scr).Path
Start-Process "rundll32.exe" -ArgumentList "desk.cpl,InstallScreenSaver `"$full`""
Write-Host "Opened Screen Saver Settings with $full"
