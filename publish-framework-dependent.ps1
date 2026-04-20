# ===================================================================
#      Publish WhisperInk as a framework-dependent single EXE
# ===================================================================
# Smaller than the self-contained build (~5 MB vs ~80 MB) but requires
# the .NET 8 **Desktop** Runtime on the target machine (not just the
# base runtime — WPF needs the Desktop variant).
# ===================================================================
$ErrorActionPreference = "Stop"

$ProjectName = "WhisperInk"
$repoRoot    = Split-Path -Parent $PSCommandPath
$ProjectFile = Join-Path $repoRoot "$ProjectName.csproj"
$PublishDir  = Join-Path $repoRoot "_publish-fd"

Write-Host "----------------------------------" -ForegroundColor Cyan
Write-Host "Publishing (framework-dependent): $ProjectName"
Write-Host "----------------------------------" -ForegroundColor Cyan
Write-Host "Project: $ProjectFile"
Write-Host "Output:  $PublishDir"
Write-Host ""

if (-not (Test-Path $ProjectFile)) {
    Write-Host "ERROR: Project file not found at '$ProjectFile'." -ForegroundColor Red
    exit 1
}

if (Test-Path $PublishDir) {
    Write-Host "Cleaning old publish folder..." -ForegroundColor Yellow
    Remove-Item -Recurse -Force $PublishDir
}

& dotnet publish $ProjectFile `
    -c Release `
    -r win-x64 `
    --self-contained false `
    -p:PublishSingleFile=true `
    -o $PublishDir

if ($LASTEXITCODE -ne 0) {
    Write-Host "----------------------------------" -ForegroundColor Red
    Write-Host "ERROR: Publish failed (exit $LASTEXITCODE)." -ForegroundColor Red
    Write-Host "----------------------------------" -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host "----------------------------------" -ForegroundColor Green
Write-Host "Publish succeeded." -ForegroundColor Green
Write-Host "EXE: $(Join-Path $PublishDir "$ProjectName.exe")"
Write-Host "----------------------------------" -ForegroundColor Green
