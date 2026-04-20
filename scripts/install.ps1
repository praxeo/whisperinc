# ===================================================================
#      Full install: publish WhisperInk + create shortcuts
# ===================================================================
# Runs the self-contained publish, then creates a Start Menu shortcut
# and (with -Desktop) a Desktop shortcut.
#
#   .\scripts\install.ps1           # publish + Start Menu only
#   .\scripts\install.ps1 -Desktop  # + Desktop shortcut
# ===================================================================
[CmdletBinding()]
param([switch]$Desktop)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$publishScript   = Join-Path $repoRoot "publish.ps1"
$shortcutsScript = Join-Path $PSScriptRoot "install-shortcuts.ps1"

Write-Host "==============================================" -ForegroundColor Cyan
Write-Host "WhisperInk installer" -ForegroundColor Cyan
Write-Host "==============================================" -ForegroundColor Cyan

Write-Host ""
Write-Host "[1/2] Building self-contained release..." -ForegroundColor Cyan
& $publishScript
if ($LASTEXITCODE -ne 0) {
    Write-Host "Publish step failed. Aborting install." -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host ""
Write-Host "[2/2] Creating shortcuts..." -ForegroundColor Cyan
if ($Desktop) {
    & $shortcutsScript -Desktop
} else {
    & $shortcutsScript
}
if ($LASTEXITCODE -ne 0) {
    Write-Host "Shortcut step failed." -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host ""
Write-Host "==============================================" -ForegroundColor Green
Write-Host "WhisperInk is installed." -ForegroundColor Green
Write-Host "==============================================" -ForegroundColor Green
Write-Host ""
Write-Host "Next steps:"
Write-Host "  1. Launch WhisperInk from the Start Menu (or Desktop if you used -Desktop)."
Write-Host "  2. Right-click the tray icon to pick a provider, open settings, or exit."
Write-Host "  3. Pin to the taskbar from the shortcut's right-click menu if you want."
Write-Host "  4. Hold Ctrl+Space to dictate into any window."
Write-Host ""
Write-Host "Config lives at: $env:APPDATA\.WhisperInk\"
