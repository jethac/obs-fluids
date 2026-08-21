# Publish Ink Container as a folder + screensaver
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$out = Join-Path $root "dist\InkContainer"
dotnet publish (Join-Path $root "host\InkContainer.csproj") `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -o $out

Copy-Item (Join-Path $out "InkContainer.exe") (Join-Path $out "InkContainer.scr") -Force

Write-Host "Built: $out"
Write-Host "  InkContainer.exe     native D3D11 / OBS Game Capture (--obs)"
Write-Host "  InkContainer.scr     right-click > Install"
