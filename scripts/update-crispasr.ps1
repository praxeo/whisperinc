# ===================================================================
# update-crispasr.ps1 - deploy a prebuilt CrispASR release binary
# ===================================================================
# Downloads a release asset from CrispStrobe/CrispASR, backs up the
# currently deployed crispasr.exe + DLLs, swaps in the new build, and
# smoke-tests it. GGUF models and everything else in the deploy folder
# are left untouched.
#
# Requires: gh CLI (authenticated). Windows PowerShell 5.1 compatible.
# NOTE: keep this file pure ASCII - PS 5.1 reads BOM-less files as ANSI
# and multi-byte punctuation turns into stray smart-quote bytes that
# break the parser.
#
# Usage:
#   .\update-crispasr.ps1                          # pinned tag, CUDA build
#   .\update-crispasr.ps1 -Tag v0.7.1 -Asset crispasr-windows-x86_64-vulkan.zip
#   .\update-crispasr.ps1 -Tag v0.8.0              # future release, CUDA build
#
# On smoke-test failure the previous binaries are restored automatically.

param(
    [string]$Tag   = "v0.7.1",
    [string]$Asset = "crispasr-windows-x86_64-cuda.zip"
)

$ErrorActionPreference = "Stop"

$deployDir = Join-Path $env:APPDATA ".WhisperInk\cohere-gguf"
$stamp     = Get-Date -Format "yyyy-MM-dd-HHmm"
$tmp       = Join-Path $env:TEMP ("crispasr-update-" + $stamp)

if (-not (Test-Path $deployDir)) {
    throw "Deploy folder not found: $deployDir (run WhisperInk once, or create it manually)"
}

New-Item -ItemType Directory -Force -Path $tmp | Out-Null

# --- 1. Download the release asset ---------------------------------
Write-Host "Downloading $Asset from CrispStrobe/CrispASR $Tag ..."
gh release download $Tag --repo CrispStrobe/CrispASR --pattern $Asset --dir $tmp
if ($LASTEXITCODE -ne 0) { throw "gh release download failed (tag=$Tag asset=$Asset)" }

$zipPath = Join-Path $tmp $Asset
if (-not (Test-Path $zipPath)) { throw "Downloaded asset not found at $zipPath" }

# --- 2. Stop any crispasr servers running from the deploy dir ------
# WhisperInk respawns its server on the next dictation, so this is safe
# even while the app is running. Wait-Process matters: Stop-Process
# returns before the OS releases the exe file lock.
$running = @(Get-Process -Name crispasr -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -like "$deployDir*" })
if ($running.Count -gt 0) {
    $running | Stop-Process -Force -Confirm:$false
    $running | Wait-Process -Timeout 15 -ErrorAction SilentlyContinue
    Write-Host "Stopped $($running.Count) running crispasr server(s)."
}

# --- 3. Back up current exe + DLLs ----------------------------------
$bak = Join-Path $deployDir (".old-" + $stamp)
$existing = @()
$exePath = Join-Path $deployDir "crispasr.exe"
if (Test-Path $exePath) { $existing += Get-Item $exePath }
$existing += @(Get-ChildItem $deployDir -Filter *.dll -ErrorAction SilentlyContinue)

if ($existing.Count -gt 0) {
    New-Item -ItemType Directory -Force -Path $bak | Out-Null
    foreach ($f in $existing) { Copy-Item $f.FullName $bak -Force }
    Write-Host "Backed up $($existing.Count) file(s) to $bak"
} else {
    Write-Host "No existing binaries to back up (fresh deploy)."
}

# --- 4. Remove old binaries (exe + DLLs ONLY - GGUFs untouched) -----
# Delete-before-copy matters: the new release's DLL set may differ and
# stale leftovers cause mixed-version loads. Retries cover the window
# where a just-killed server still holds the exe lock.
foreach ($f in $existing) {
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

# --- 5. Extract and deploy ------------------------------------------
$extractDir = Join-Path $tmp "x"
Expand-Archive -Path $zipPath -DestinationPath $extractDir -Force

$newExe = Get-ChildItem $extractDir -Recurse -Filter crispasr.exe | Select-Object -First 1
if ($null -eq $newExe) { throw "crispasr.exe not found inside $Asset" }

$srcDir = $newExe.DirectoryName
Copy-Item $newExe.FullName $deployDir -Force
Get-ChildItem $srcDir -Filter *.dll | ForEach-Object { Copy-Item $_.FullName $deployDir -Force }

# --- 6. Smoke test ---------------------------------------------------
# Empty output on --help is the STATUS_DLL_NOT_FOUND (-1073741515)
# signature: the exe dies before main() with nothing on stdout/stderr.
$outFile = Join-Path $tmp "help-out.txt"
$errFile = Join-Path $tmp "help-err.txt"
$proc = Start-Process -FilePath (Join-Path $deployDir "crispasr.exe") `
    -ArgumentList "--help" `
    -RedirectStandardOutput $outFile -RedirectStandardError $errFile `
    -NoNewWindow -PassThru -Wait

$outLen = (Get-Item $outFile).Length
$errLen = (Get-Item $errFile).Length
$smokeOk = ($proc.ExitCode -eq 0) -and (($outLen -gt 0) -or ($errLen -gt 0))

if (-not $smokeOk) {
    Write-Warning "Smoke test FAILED (exit=$($proc.ExitCode), stdout=$outLen B, stderr=$errLen B) - restoring backup."
    Get-ChildItem $deployDir -Filter *.dll | Remove-Item -Force -Confirm:$false
    Remove-Item (Join-Path $deployDir "crispasr.exe") -Force -Confirm:$false -ErrorAction SilentlyContinue
    if (Test-Path $bak) {
        Get-ChildItem $bak | ForEach-Object { Copy-Item $_.FullName $deployDir -Force }
    }
    throw "New binary failed smoke test; previous binaries restored from $bak"
}

# --- 7. Report -------------------------------------------------------
Write-Host ""
Write-Host "Deployed $Tag ($Asset):"
Get-ChildItem $deployDir | Where-Object { $_.Name -eq "crispasr.exe" -or $_.Extension -eq ".dll" } |
    Select-Object Name, @{n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } }, LastWriteTime |
    Format-Table -AutoSize
Write-Host "Backup: $bak"
Write-Host "Temp:   $tmp (safe to delete)"
