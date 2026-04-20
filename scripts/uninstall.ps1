# ===================================================================
#      Uninstall WhisperInk shortcuts and published binaries
# ===================================================================
# Never touches %APPDATA%\.WhisperInk\ — config, history, logs, and
# GGUF models are preserved. Pass -RemoveBinaries to also delete the
# _publish and _publish-fd output folders.
#
#   .\scripts\uninstall.ps1                    # just remove shortcuts
#   .\scripts\uninstall.ps1 -RemoveBinaries    # + wipe _publish*
# ===================================================================
[CmdletBinding()]
param([switch]$RemoveBinaries)

$ErrorActionPreference = "Stop"

$repoRoot   = Split-Path -Parent $PSScriptRoot
$startMenu  = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
$desktopDir = [Environment]::GetFolderPath("Desktop")

$shortcuts = @(
    (Join-Path $startMenu  "WhisperInk.lnk"),
    (Join-Path $desktopDir "WhisperInk.lnk")
)

Write-Host "Removing shortcuts..." -ForegroundColor Cyan
foreach ($s in $shortcuts) {
    if (Test-Path $s) {
        Remove-Item -LiteralPath $s -Force
        Write-Host "  removed: $s" -ForegroundColor Yellow
    } else {
        Write-Host "  skipped (not present): $s" -ForegroundColor DarkGray
    }
}

# Remove the Run-at-login registry entry (if present). Safe no-op
# otherwise — the auto-start setting inside WhisperInk writes here.
$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
try {
    $val = Get-ItemProperty -Path $runKey -Name "WhisperInk" -ErrorAction SilentlyContinue
    if ($val) {
        Remove-ItemProperty -Path $runKey -Name "WhisperInk" -Force
        Write-Host "Removed auto-start entry: HKCU\...\Run\WhisperInk" -ForegroundColor Yellow
    }
} catch { }

if ($RemoveBinaries) {
    foreach ($dir in @("_publish", "_publish-fd")) {
        $p = Join-Path $repoRoot $dir
        if (Test-Path $p) {
            Remove-Item -Recurse -Force $p
            Write-Host "Removed $p" -ForegroundColor Yellow
        }
    }
}

Write-Host ""
Write-Host "Uninstall complete. Config preserved at:" -ForegroundColor Green
Write-Host "  $env:APPDATA\.WhisperInk\" -ForegroundColor Green
Write-Host "Delete that folder manually if you want a truly clean wipe."
