# ===================================================================
#      Publish WhisperInk as a single EXE (self-contained)
# ===================================================================

$ProjectName = "WhisperInk"

$SolutionDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$ProjectFile = Join-Path $SolutionDir $ProjectName "$ProjectName.csproj"
$PublishDir = Join-Path $SolutionDir "_publish"

Write-Host "----------------------------------" -ForegroundColor Cyan
Write-Host "Publishing: $ProjectName"
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

$Arguments = @(
    "publish",
    $ProjectFile,
    "-c", "Release",
    "-r", "win-x64",
    "--self-contained", "true",
    "-p:PublishSingleFile=true",
    "-p:PublishTrimmed=false",
    "-p:PublishReadyToRun=true",
    "-o", $PublishDir
)

Write-Host "Running:" -ForegroundColor Green
Write-Host "dotnet $Arguments"
Write-Host ""

& dotnet $Arguments

if ($?) {
    Write-Host "----------------------------------" -ForegroundColor Green
    Write-Host "Publish succeeded!" -ForegroundColor Green
    Write-Host "EXE location: $PublishDir"
    Write-Host "----------------------------------" -ForegroundColor Green
    Invoke-Item $PublishDir
} else {
    Write-Host "----------------------------------" -ForegroundColor Red
    Write-Host "ERROR: Publish failed." -ForegroundColor Red
    Write-Host "----------------------------------" -ForegroundColor Red
}
