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

# Bootstrap Visual Studio Developer environment so cmake/cl/msbuild are on PATH.
# VS bundles cmake at Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin
# but doesn't add it to system PATH; entering the DevShell exports everything.
if (-not (Get-Command cmake -ErrorAction SilentlyContinue)) {
    $vsWhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path $vsWhere)) {
        throw "vswhere.exe not found. Install Visual Studio 2022 with 'Desktop development with C++'."
    }
    $vsInstall = & $vsWhere -latest -property installationPath
    if (-not $vsInstall) { throw "No VS 2022 installation found by vswhere." }
    $devShellModule = Join-Path $vsInstall "Common7\Tools\Microsoft.VisualStudio.DevShell.dll"
    if (-not (Test-Path $devShellModule)) { throw "DevShell module missing at $devShellModule" }
    Import-Module $devShellModule
    Enter-VsDevShell -VsInstallPath $vsInstall -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -no_logo' | Out-Null
    Write-Host "Entered VS Developer Shell at $vsInstall" -ForegroundColor Cyan
}

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
# Both CUDA and Vulkan are auto-detected. Each is enabled iff the
# corresponding SDK is present, so the same script handles:
#   - CPU-only laptop (no CUDA, no Vulkan SDK)
#   - iGPU/dGPU offload via Vulkan (Vulkan SDK present)
#   - NVIDIA workstation (CUDA Toolkit present)
#   - both (CUDA + Vulkan SDK both present)
#
# Vulkan winget package: "KhronosGroup.VulkanSDK"
# CUDA: install CUDA Toolkit 12.x or newer (sets CUDA_PATH).
$cudaFlag = "OFF"
$nvcc = Get-Command nvcc -ErrorAction SilentlyContinue
if ($nvcc -or $env:CUDA_PATH) {
    $cudaFlag = "ON"
    Write-Host "CUDA Toolkit detected -- enabling -DGGML_CUDA=ON" -ForegroundColor Cyan
} else {
    Write-Host "CUDA Toolkit not detected -- building without CUDA" -ForegroundColor Yellow
}
$vulkanFlag = "OFF"
if ($env:VULKAN_SDK -and (Test-Path "$env:VULKAN_SDK\Bin\vulkaninfo.exe")) {
    $vulkanFlag = "ON"
    Write-Host "Vulkan SDK detected -- enabling -DGGML_VULKAN=ON" -ForegroundColor Cyan
} else {
    Write-Host "Vulkan SDK not detected -- building without Vulkan" -ForegroundColor Yellow
}
cmake -B build -G "Visual Studio 17 2022" -A x64 "-DGGML_CUDA=$cudaFlag" "-DGGML_VULKAN=$vulkanFlag" -DWHISPER_BUILD_TESTS=OFF
if ($LASTEXITCODE -ne 0) { Pop-Location; throw "cmake configure failed (exit $LASTEXITCODE)" }
cmake --build build --config Release --target crispasr-cli
if ($LASTEXITCODE -ne 0) { Pop-Location; throw "cmake build failed (exit $LASTEXITCODE)" }
Pop-Location

$releaseDir = Join-Path $srcRoot "build\bin\Release"
if (-not (Test-Path $releaseDir)) { throw "Release dir not found: $releaseDir" }
$exe = Join-Path $releaseDir "crispasr.exe"
if (-not (Test-Path $exe)) { $exe = Join-Path $releaseDir "whisper-cli.exe" }
if (-not (Test-Path $exe)) { throw "Neither crispasr.exe nor whisper-cli.exe found under $releaseDir" }

New-Item -ItemType Directory -Force -Path $deployDir | Out-Null
Copy-Item $exe $deployDir -Force
Get-ChildItem $releaseDir -Filter *.dll | ForEach-Object { Copy-Item $_.FullName $deployDir -Force }

Write-Host "Deployed to ${deployDir}:" -ForegroundColor Green
Get-ChildItem $deployDir | Select-Object Name, Length | Format-Table
