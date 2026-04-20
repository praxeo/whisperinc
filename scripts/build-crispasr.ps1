# build-crispasr.ps1
# Build CrispASR (whisper.cpp fork) on Windows and deploy the binary + DLLs
# alongside the Cohere/Parakeet GGUF model so the transcribers find everything.
#
# Prerequisites:
#   - Visual Studio 2022 (or Build Tools) with "Desktop development with C++"
#   - CMake 3.14+
#   - Git
#
# Clones CrispASR as a sibling of the whisperinc repo this script lives in,
# so the two source trees stay together. For a CUDA build on a GPU box,
# change -DGGML_CUDA=OFF to ON below.
$ErrorActionPreference = "Stop"

$whisperincRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
$srcRoot        = Join-Path (Split-Path -Parent $whisperincRoot) "CrispASR"
$repo           = "https://github.com/CrispStrobe/CrispASR"
$deployDir      = Join-Path $env:APPDATA ".WhisperInk\cohere-gguf"

if (-not (Test-Path $srcRoot)) {
    Write-Host "Cloning CrispASR -> $srcRoot" -ForegroundColor Cyan
    git clone $repo $srcRoot
} else {
    Write-Host "Updating CrispASR in $srcRoot" -ForegroundColor Cyan
    Push-Location $srcRoot
    git pull --ff-only
    Pop-Location
}

Push-Location $srcRoot
cmake -B build -G "Visual Studio 17 2022" -A x64 -DGGML_CUDA=OFF -DWHISPER_BUILD_TESTS=OFF
cmake --build build --config Release --target whisper-cli
Pop-Location

$releaseDir = Join-Path $srcRoot "build\bin\Release"
if (-not (Test-Path $releaseDir)) { throw "Release dir not found: $releaseDir" }
$exe = Join-Path $releaseDir "crispasr.exe"
if (-not (Test-Path $exe)) { $exe = Join-Path $releaseDir "whisper-cli.exe" }
if (-not (Test-Path $exe)) { throw "Neither crispasr.exe nor whisper-cli.exe found under $releaseDir" }

New-Item -ItemType Directory -Force -Path $deployDir | Out-Null
Copy-Item $exe $deployDir -Force
Get-ChildItem $releaseDir -Filter *.dll | ForEach-Object { Copy-Item $_.FullName $deployDir -Force }

Write-Host "Deployed to $deployDir:" -ForegroundColor Green
Get-ChildItem $deployDir | Select-Object Name, Length | Format-Table
