# download-cohere-gguf.ps1
# Download the Cohere Transcribe 03-2026 GGUF into the WhisperInk model folder
# alongside the CrispASR binary.
#
# Q5_0 is the sweet spot: 1.45 GB, RTFx ~1.06× on 8 CPU threads, quality nearly
# identical to F16. Swap to Q4_K for smaller footprint or Q6_K/Q8_0 if you want
# more quality headroom.

$ErrorActionPreference = "Stop"

$modelDir = Join-Path $env:APPDATA ".WhisperInk\cohere-gguf"
New-Item -ItemType Directory -Force -Path $modelDir | Out-Null

$variant = "q5_0"
$file    = "cohere-transcribe-$variant.gguf"
$url     = "https://huggingface.co/cstr/cohere-transcribe-03-2026-GGUF/resolve/main/$file"
$dest    = Join-Path $modelDir $file

if (Test-Path $dest) {
    $sizeMB = [math]::Round((Get-Item $dest).Length / 1MB, 1)
    Write-Host "Already downloaded: $dest ($sizeMB MB)" -ForegroundColor Yellow
} else {
    Write-Host "Downloading $file (~1.45 GB) → $dest" -ForegroundColor Cyan
    # curl.exe is in base Windows 10+ and is faster than Invoke-WebRequest for large files.
    & curl.exe -L --fail --progress-bar -o $dest $url
    if ($LASTEXITCODE -ne 0) { throw "Download failed (exit $LASTEXITCODE)" }
    $sizeMB = [math]::Round((Get-Item $dest).Length / 1MB, 1)
    Write-Host "Done: $sizeMB MB" -ForegroundColor Green
}

Write-Host "Model ready at: $dest" -ForegroundColor Green
