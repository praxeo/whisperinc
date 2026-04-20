# ===================================================================
#      Publish WhisperInk as a self-contained single EXE
# ===================================================================
# Produces a standalone build in $repoRoot\_publish\ that includes the
# .NET 8 runtime and every dependency. Roughly 80 MB. Target machine
# does not need the .NET 8 Desktop Runtime installed.
# ===================================================================
$ErrorActionPreference = "Stop"

$ProjectName = "WhisperInk"
$repoRoot    = Split-Path -Parent $PSCommandPath
$ProjectFile = Join-Path $repoRoot "$ProjectName.csproj"
$PublishDir  = Join-Path $repoRoot "_publish"

Write-Host "----------------------------------" -ForegroundColor Cyan
Write-Host "Publishing (self-contained): $ProjectName"
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
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:PublishTrimmed=false `
    -p:PublishReadyToRun=true `
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
