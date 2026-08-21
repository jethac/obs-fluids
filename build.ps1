# Build native Rust + Vulkan (wgpu) host and copy a Windows screensaver.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

cargo build --release
$out = Join-Path $root "dist\InkContainer"
New-Item -ItemType Directory -Force -Path $out | Out-Null
$exe = Join-Path $root "target\release\ink-container.exe"
Copy-Item $exe (Join-Path $out "InkContainer.exe") -Force
Copy-Item $exe (Join-Path $out "InkContainer.scr") -Force

Write-Host "Built: $out"
Write-Host "  InkContainer.exe     Vulkan (wgpu) / OBS Game Capture (--obs)"
Write-Host "  InkContainer.scr     right-click > Install"
