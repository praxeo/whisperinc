$ErrorActionPreference = "Stop"
$dir = Join-Path $env:APPDATA ".WhisperInk\cohere-gguf"
$dst = Join-Path $dir "cohere-transcribe-q6_k.gguf"
New-Item -ItemType Directory -Force -Path $dir | Out-Null
if (Test-Path $dst) {
    $mb = [math]::Round((Get-Item $dst).Length / 1MB, 1)
    Write-Host "Already exists: $dst ($mb MB)" -ForegroundColor Yellow
} else {
    Write-Host "Downloading Q6_K (~1.62 GB, near-F16 accuracy)..." -ForegroundColor Cyan
    & curl.exe -L --fail -o $dst "https://huggingface.co/cstr/cohere-transcribe-03-2026-GGUF/resolve/main/cohere-transcribe-q6_k.gguf"
    if ($LASTEXITCODE -ne 0) { throw "Download failed (exit $LASTEXITCODE)" }
    $mb = [math]::Round((Get-Item $dst).Length / 1MB, 1)
    Write-Host "Done: $dst ($mb MB)" -ForegroundColor Green
}
