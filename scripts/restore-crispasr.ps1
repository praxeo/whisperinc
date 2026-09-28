# ===================================================================
# restore-crispasr.ps1 - put back CrispASR binaries from a backup
# ===================================================================
# Undoes update-crispasr.ps1: stops any crispasr server running from the
# deploy folder, removes its crispasr.exe + DLLs, copies back the ones in
# the backup folder (a .old-<stamp> folder update-crispasr.ps1 made) and
# smoke-tests the result. GGUF models and everything else in the deploy
# folder are left untouched.
#
# Windows PowerShell 5.1 compatible.
# NOTE: keep this file pure ASCII - PS 5.1 reads BOM-less files as ANSI.
#
# Usage:
#   .\restore-crispasr.ps1 -Backup "$env:APPDATA\.WhisperInk\cohere-gguf\.old-2026-09-28-1302"
#                                                  # (v0.8.30, replaced by v0.8.38 on 2026-09-28)
#
# -DeployDir is for testing the script on a copy.

param(
    [Parameter(Mandatory = $true)][string]$Backup,
    [string]$DeployDir = ""
)

$ErrorActionPreference = "Stop"

$deployDir = $DeployDir
if (-not $deployDir) { $deployDir = Join-Path $env:APPDATA ".WhisperInk\cohere-gguf" }
if (-not (Test-Path (Join-Path $Backup "crispasr.exe"))) { throw "No crispasr.exe in the backup folder $Backup" }
$Backup = (Resolve-Path $Backup).Path

# --- 1. Stop any crispasr servers running from the deploy dir ------
$running = @(Get-Process -Name crispasr -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -like "$deployDir*" })
if ($running.Count -gt 0) {
    $running | Stop-Process -Force -Confirm:$false
    $running | Wait-Process -Timeout 15 -ErrorAction SilentlyContinue
    Write-Host "Stopped $($running.Count) running crispasr server(s)."
}

# --- 2. Remove the current exe + DLLs (GGUFs untouched) -------------
$current = @()
$exePath = Join-Path $deployDir "crispasr.exe"
if (Test-Path $exePath) { $current += Get-Item $exePath }
$current += @(Get-ChildItem $deployDir -Filter *.dll -ErrorAction SilentlyContinue)
foreach ($f in $current) {
    $tries = 0
    while ($true) {
        try {
            Remove-Item $f.FullName -Force -Confirm:$false -ErrorAction Stop
            break
        } catch {
            $tries++
            if ($tries -ge 10) { throw "Cannot delete $($f.Name) after $tries attempts: $($_.Exception.Message)" }
            Start-Sleep -Milliseconds 500
        }
    }
}

# --- 3. Copy the backup back ----------------------------------------
Get-ChildItem $Backup -File | Where-Object { $_.Name -eq "crispasr.exe" -or $_.Extension -eq ".dll" } |
    ForEach-Object { Copy-Item $_.FullName $deployDir -Force }

# --- 4. Smoke test ---------------------------------------------------
$outFile = Join-Path $env:TEMP "crispasr-restore-help-out.txt"
$errFile = Join-Path $env:TEMP "crispasr-restore-help-err.txt"
$proc = Start-Process -FilePath $exePath -ArgumentList "--help" `
    -RedirectStandardOutput $outFile -RedirectStandardError $errFile `
    -NoNewWindow -PassThru -Wait
$outLen = (Get-Item $outFile).Length
$errLen = (Get-Item $errFile).Length
if (($proc.ExitCode -ne 0) -or (($outLen -eq 0) -and ($errLen -eq 0))) {
    throw "Restored binary failed its smoke test (exit=$($proc.ExitCode), stdout=$outLen B, stderr=$errLen B)"
}

Write-Host ""
Write-Host "Restored from ${Backup}:"
Get-ChildItem $deployDir | Where-Object { $_.Name -eq "crispasr.exe" -or $_.Extension -eq ".dll" } |
    Select-Object Name, @{n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } }, LastWriteTime |
    Format-Table -AutoSize
