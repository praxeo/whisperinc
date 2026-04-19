# build-crispasr.ps1
# Build CrispASR (whisper.cpp fork) on Windows and deploy the binary + DLLs
# alongside the Cohere GGUF model so CohereGgufTranscriber.cs finds everything.
#
# Prerequisites:
#   - Visual Studio 2022 with "Desktop development with C++" workload
#     (or standalone Build Tools 2022)
#   - CMake 3.14+
#   - Git
#
# For the 3090 workstation you can change -DGGML_CUDA=OFF to ON below.
# For the locked-down i7-13700T work PCs, CPU build is what you want.

$ErrorActionPreference = "Stop"

$srcRoot    = "C:\Users\obert\OneDrive\Desktop\CrispASR"
$repo       = "https://github.com/CrispStrobe/CrispASR"
$deployDir  = Join-Path $env:APPDATA ".WhisperInk\cohere-gguf"

# ── Clone or update ────────────────────────────────────────────────────
if (-not (Test-Path $srcRoot)) {
    Write-Host "Cloning CrispASR → $srcRoot" -ForegroundColor Cyan
    git clone $repo $srcRoot
} else {
    Write-Host "Updating CrispASR in $srcRoot" -ForegroundColor Cyan
    Push-Location $srcRoot
    git pull --ff-only
    Pop-Location
}

# ── Configure + build ──────────────────────────────────────────────────
Push-Location $srcRoot

cmake -B build -G "Visual Studio 16 2019" -A x64 -DGGML_CUDA=OFF
cmake --build build --config Release --target whisper-cli -- /m

Pop-Location

# ── Locate the built binary (MSVC multi-config layout varies) ─────────
$exe = Get-ChildItem -Path (Join-Path $srcRoot "build\bin\Release") -Filter "crispasr.exe" -ErrorAction SilentlyContinue |
       Select-Object -First 1
if (-not $exe) {
    $exe = Get-ChildItem -Path (Join-Path $srcRoot "build") -Filter "crispasr.exe" -Recurse |
           Where-Object { $_.FullName -notmatch '\\Debug\\' } |
           Select-Object -First 1
}
if (-not $exe) {
    # Fallback: whisper-cli alias
    $exe = Get-ChildItem -Path (Join-Path $srcRoot "build") -Filter "whisper-cli.exe" -Recurse |
           Select-Object -First 1
}
if (-not $exe) {
    throw "Build succeeded but no crispasr.exe / whisper-cli.exe found under $srcRoot\build"
}
Write-Host "Built: $($exe.FullName)" -ForegroundColor Green

# ── Deploy into %APPDATA%\.WhisperInk\cohere-gguf\ ─────────────────────
New-Item -ItemType Directory -Force -Path $deployDir | Out-Null
$binDir = Split-Path $exe.FullName -Parent

# Copy the exe (normalize name to crispasr.exe)
Copy-Item -Path $exe.FullName -Destination (Join-Path $deployDir "crispasr.exe") -Force

# Copy any ggml backend DLLs that the exe links against dynamically.
# Release builds typically produce ggml.dll, ggml-cpu.dll, ggml-base.dll, etc.
Get-ChildItem -Path $binDir -Filter "*.dll" -ErrorAction SilentlyContinue |
    ForEach-Object { Copy-Item -Path $_.FullName -Destination $deployDir -Force }

Write-Host "Deployed to: $deployDir" -ForegroundColor Green
Get-ChildItem $deployDir | Select-Object Name, @{N='MB';E={[math]::Round($_.Length/1MB,1)}} | Format-Table
