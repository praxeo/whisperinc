# ===================================================================
#  Create Start Menu (and optionally Desktop) shortcuts for WhisperInk
# ===================================================================
# Idempotent — re-running replaces existing shortcuts cleanly.
#
#   .\scripts\install-shortcuts.ps1           # Start Menu only
#   .\scripts\install-shortcuts.ps1 -Desktop  # + Desktop shortcut
# ===================================================================
[CmdletBinding()]
param(
    [switch]$Desktop,
    [string]$ExePath
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($ExePath)) {
    $candidates = @(
        (Join-Path $repoRoot "_publish\WhisperInk.exe"),
        (Join-Path $repoRoot "_publish-fd\WhisperInk.exe"),
        (Join-Path $repoRoot "bin\Release\net8.0-windows\win-x64\publish\WhisperInk.exe"),
        (Join-Path $repoRoot "bin\Release\net8.0-windows\WhisperInk.exe")
    )
    $ExePath = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}

if ([string]::IsNullOrWhiteSpace($ExePath) -or -not (Test-Path $ExePath)) {
    Write-Host "ERROR: WhisperInk.exe not found. Run .\publish.ps1 first, or pass -ExePath." -ForegroundColor Red
    Write-Host "Tried:" -ForegroundColor DarkGray
    $candidates | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }
    exit 1
}

$ExePath = (Resolve-Path $ExePath).Path
Write-Host "Target EXE: $ExePath" -ForegroundColor Cyan

$startMenu   = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
$desktopDir  = [Environment]::GetFolderPath("Desktop")
$shortcuts   = @(@{ Path = Join-Path $startMenu "WhisperInk.lnk"; Kind = "Start Menu" })
if ($Desktop) {
    $shortcuts += @{ Path = Join-Path $desktopDir "WhisperInk.lnk"; Kind = "Desktop" }
}

$shell = New-Object -ComObject WScript.Shell
foreach ($s in $shortcuts) {
    if (Test-Path $s.Path) {
        Remove-Item -LiteralPath $s.Path -Force
    }
    $sc = $shell.CreateShortcut($s.Path)
    $sc.TargetPath        = $ExePath
    $sc.WorkingDirectory  = Split-Path -Parent $ExePath
    $sc.IconLocation      = "$ExePath,0"
    $sc.Description       = "System-wide dictation for Windows"
    $sc.WindowStyle       = 1
    $sc.Save()
    Write-Host ("  {0,-11} -> {1}" -f $s.Kind, $s.Path) -ForegroundColor Green
}

Write-Host "Shortcuts installed." -ForegroundColor Green
