# setup-reson8.ps1 - install the Reson8 API key into WhisperInk's config and
# (optionally) run the clinical-clip evaluation immediately afterwards.
#
# The key is taken from the CLIPBOARD by default, so running this requires no
# typing and no dialog navigation - one command does the whole job. It is never
# printed, never logged, and never written anywhere except the ApiKey field of
# the reson8 provider in config.json.
#
# Usage (clipboard, the zero-typing path):
#   powershell -ExecutionPolicy Bypass -File _scratch\reson8\setup-reson8.ps1
#
# Other sources, if the clipboard is inconvenient:
#   ... setup-reson8.ps1 -Key <key>
#   $env:RESON8_API_KEY = '<key>'; ... setup-reson8.ps1
#
# Switches:
#   -NoTest     install only; skip the clip evaluation
#   -NoLaunch   do not relaunch WhisperInk afterwards
#
# Safety: config.json is backed up before every write, and the rewritten file
# is validated (re-parses, same provider count, ActiveProviderId unchanged)
# before it replaces the original. PowerShell 5.1's ConvertTo-Json defaults to
# -Depth 2, which would silently flatten the nested provider objects into
# "System.Object[]" strings and destroy the config - hence -Depth 100 plus the
# post-write validation.
#
# Pure ASCII on purpose: PowerShell 5.1 reads BOM-less files as ANSI, and
# multi-byte punctuation decodes into stray bytes that break the parser.

[CmdletBinding()]
param(
    [string] $Key        = '',
    [string] $ConfigPath = '',
    [string] $ProviderId = 'reson8',
    [switch] $NoTest,
    [switch] $NoLaunch
)

$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $PSCommandPath
$repo = Resolve-Path (Join-Path $here '..\..') | Select-Object -ExpandProperty Path
if (-not $ConfigPath) { $ConfigPath = Join-Path $env:APPDATA '.WhisperInk\config.json' }

# ------------------------------------------------------------ get the key ---
$source = 'parameter'
if (-not $Key -and $env:RESON8_API_KEY) { $Key = $env:RESON8_API_KEY; $source = 'RESON8_API_KEY' }
if (-not $Key) {
    try { $Key = (Get-Clipboard -Raw -ErrorAction Stop); $source = 'clipboard' } catch { }
}
if ($Key) { $Key = $Key.Trim() }

if (-not $Key) {
    Write-Host ""
    Write-Host "No API key found." -ForegroundColor Yellow
    Write-Host "Copy the key to the clipboard and run this again, or pass -Key <key>."
    Write-Host ""
    exit 1
}

# Cheap sanity checks. A key pasted with surrounding text or a newline is the
# common failure, and it would otherwise surface much later as an opaque 401.
if ($Key -match '\s') {
    Write-Host "The value from the $source contains whitespace - that is probably not just a key." -ForegroundColor Yellow
    Write-Host "Pass it explicitly with -Key <key> instead."
    exit 1
}
if ($Key.Length -lt 16 -or $Key.Length -gt 200) {
    Write-Host "The value from the $source is $($Key.Length) chars, which does not look like an API key." -ForegroundColor Yellow
    Write-Host "Pass it explicitly with -Key <key> instead."
    exit 1
}

Write-Host ""
Write-Host "Reson8 setup" -ForegroundColor Cyan
Write-Host ("  key source : {0} ({1} chars, not displayed)" -f $source, $Key.Length)
Write-Host "  config     : $ConfigPath"

# --------------------------------------------------------- app must be off ---
# WhisperInk rewrites config.json from memory when settings change, so editing
# it underneath a running instance risks the key being clobbered later.
if (Get-Process WhisperInk -ErrorAction SilentlyContinue) {
    Write-Host ""
    Write-Host "WhisperInk is running - stopping it so the config write is not clobbered." -ForegroundColor Yellow
    $p = Get-Process WhisperInk -ErrorAction SilentlyContinue
    $p.CloseMainWindow() | Out-Null
    Start-Sleep -Milliseconds 1500
    if (Get-Process WhisperInk -ErrorAction SilentlyContinue) {
        Stop-Process -Name WhisperInk -Force
        Start-Sleep -Milliseconds 800
    }
}

# ----------------------------------------------------------------- config ---
if (-not (Test-Path $ConfigPath)) { Write-Error "config.json not found at $ConfigPath"; exit 1 }

$originalText = Get-Content -Raw -Path $ConfigPath
$cfg = $originalText | ConvertFrom-Json

$beforeCount  = @($cfg.Providers).Count
$beforeActive = $cfg.ActiveProviderId
$beforeIds    = @($cfg.Providers | ForEach-Object { $_.Id })

$backup = "$ConfigPath.bak-" + (Get-Date -Format 'yyyy-MM-dd-HHmmss')
Copy-Item $ConfigPath $backup -Force

$prov = $cfg.Providers | Where-Object { $_.Id -eq $ProviderId }

if ($prov) {
    $prov.ApiKey = $Key
    Write-Host "  provider   : existing '$ProviderId' entry updated"
} else {
    # Mirror AppConfig.CreateDefaults()'s reson8 preset. LoadConfig backfills
    # anything omitted, and its default-merge is keyed on Id, so adding the
    # entry here means the app does not need a separate warm-up launch.
    $new = [pscustomobject][ordered]@{
        Id                    = 'reson8'
        Name                  = 'Reson8'
        BaseUrl               = 'https://api.reson8.dev'
        ApiKey                = $Key
        TranscriptionEndpoint = 'https://api.reson8.dev/v1/speech-to-text/prerecorded'
        AuthHeaderName        = ''
        ModelFieldName        = 'model'
        TranscriptionModel    = ''
        SupportsTranscription = $true
        Language              = 'en'
        BiasMechanism         = 'reson8_phrases'
        ContextBiasMode       = 'none'
        TranscriberKind       = 'Reson8'
        Reson8ExtraParams     = [pscustomobject]@{}
    }
    $cfg.Providers = @($cfg.Providers) + $new
    Write-Host "  provider   : '$ProviderId' entry added"
}

# -Depth 100: the default of 2 would flatten the provider objects. Non-optional.
$newText = $cfg | ConvertTo-Json -Depth 100

# Validate BEFORE overwriting: re-parse, and confirm nothing was lost.
$check = $null
try { $check = $newText | ConvertFrom-Json } catch {
    Write-Host "Rewritten config failed to re-parse - aborting, original untouched." -ForegroundColor Red
    exit 1
}
$afterIds = @($check.Providers | ForEach-Object { $_.Id })
$lost = $beforeIds | Where-Object { $afterIds -notcontains $_ }
if ($lost) {
    Write-Host "Rewrite would drop provider(s): $($lost -join ', ') - aborting." -ForegroundColor Red
    exit 1
}
if ($check.ActiveProviderId -ne $beforeActive) {
    Write-Host "Rewrite would change ActiveProviderId - aborting." -ForegroundColor Red
    exit 1
}
$expected = if ($prov) { $beforeCount } else { $beforeCount + 1 }
if (@($check.Providers).Count -ne $expected) {
    Write-Host "Provider count changed unexpectedly - aborting." -ForegroundColor Red
    exit 1
}

Set-Content -Path $ConfigPath -Value $newText -Encoding UTF8

$verify = (Get-Content -Raw -Path $ConfigPath | ConvertFrom-Json).Providers |
          Where-Object { $_.Id -eq $ProviderId }
if (-not $verify -or [string]::IsNullOrWhiteSpace($verify.ApiKey)) {
    Write-Host "Key did not persist - restoring backup." -ForegroundColor Red
    Copy-Item $backup $ConfigPath -Force
    exit 1
}

Write-Host "  result     : key installed and verified" -ForegroundColor Green
Write-Host "  backup     : $backup"
Write-Host ("  providers  : {0} -> {1}, active still '{2}'" -f $beforeCount, @($check.Providers).Count, $beforeActive)
Write-Host ""

# --------------------------------------------------------------- run/exit ---
if (-not $NoTest) {
    Write-Host "Running the clinical-clip evaluation..." -ForegroundColor Cyan
    & (Join-Path $here 'run-clips.ps1')
}

if (-not $NoLaunch) {
    $exe = Join-Path $repo '_publish\WhisperInk.exe'
    if (Test-Path $exe) {
        Start-Process $exe
        Write-Host "WhisperInk relaunched." -ForegroundColor Green
    } else {
        Write-Host "Note: $exe not found - publish first, then relaunch." -ForegroundColor Yellow
    }
}
