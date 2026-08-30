# run-clips.ps1 - live evaluation of the Reson8 provider on the clinical clips.
#
# Reads the API key from %APPDATA%\.WhisperInk\config.json at runtime and never
# prints it. Nothing here needs the key to be pasted anywhere else, and the key
# is not written to any output file.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File _scratch\reson8\run-clips.ps1
#
# What it measures, per clip, in three passes:
#   none      - no phrases param at all (the unbiased baseline)
#   targeted  - a TIGHT 3-term list (just the hard terms)
#   full      - the whole ContextBiasTerms list from config.json
#
# The none-vs-targeted column answers "does phrases biasing actually flip the
# hard terms". The targeted-vs-full column tests Reson8's own warning that a
# large or off-topic list DEGRADES transcription - which, if true, is a
# behaviour none of the other providers has.
#
# Pure ASCII on purpose: PowerShell 5.1 reads BOM-less files as ANSI, and
# multi-byte punctuation decodes into stray bytes that break the parser.

[CmdletBinding()]
param(
    [string] $ConfigPath = '',
    [string] $ClipDir    = '',
    [string] $ProviderId = 'reson8',
    [int]    $Repeats    = 1
)

$ErrorActionPreference = 'Stop'

# $PSScriptRoot comes back EMPTY inside param() defaults under PowerShell 5.1,
# so resolve paths here instead. Everything is relative to this script, so the
# repo can live anywhere.
$here = Split-Path -Parent $PSCommandPath
if (-not $ConfigPath) { $ConfigPath = Join-Path $env:APPDATA '.WhisperInk\config.json' }
if (-not $ClipDir)    { $ClipDir    = Join-Path $here '..\biasing\clips' }

# ---------------------------------------------------------------- config ----
if (-not (Test-Path $ConfigPath)) {
    Write-Error "config.json not found at $ConfigPath"
    exit 1
}

$cfg = Get-Content -Raw -Path $ConfigPath | ConvertFrom-Json
$prov = $cfg.Providers | Where-Object { $_.Id -eq $ProviderId }

if (-not $prov) {
    Write-Host ""
    Write-Host "Provider '$ProviderId' is not in config.json yet." -ForegroundColor Yellow
    Write-Host "Start WhisperInk once - LoadConfig's additive default-merge adds"
    Write-Host "the shipped preset - then paste the API key into:"
    Write-Host "  tray -> Provider Settings -> Reson8 -> API key"
    Write-Host ""
    exit 1
}

if ([string]::IsNullOrWhiteSpace($prov.ApiKey)) {
    Write-Host ""
    Write-Host "Provider '$ProviderId' has no API key set." -ForegroundColor Yellow
    Write-Host "Paste it in: tray -> Provider Settings -> Reson8 -> API key"
    Write-Host "(or set the ApiKey field on that provider in config.json)"
    Write-Host ""
    exit 1
}

$key = $prov.ApiKey                       # never echoed
$endpoint = if ([string]::IsNullOrWhiteSpace($prov.TranscriptionEndpoint)) {
    'https://api.reson8.dev/v1/speech-to-text/prerecorded'
} else { $prov.TranscriptionEndpoint }

$lang = if ([string]::IsNullOrWhiteSpace($prov.Language)) { 'en' } else { $prov.Language }

# ------------------------------------------------------------- bias lists ---
# Tight list: only the terms the base model is expected to miss. Reson8's own
# guidance is that everyday words dilute the model, so this deliberately does
# NOT include the control terms.
$targeted = @('hematochezia', 'ureterolithiasis', 'biliary colic')

$full = @()
if ($cfg.ContextBiasTerms) { $full = @($cfg.ContextBiasTerms | Where-Object { $_ }) }

$passes = @(
    @{ Name = 'none';     Terms = @() }
    @{ Name = 'targeted'; Terms = $targeted }
)
if ($full.Count -gt 0) {
    $passes += @{ Name = "full($($full.Count))"; Terms = $full }
} else {
    Write-Host "note: ContextBiasTerms is empty in config.json - skipping the 'full' pass." -ForegroundColor DarkGray
}

# ------------------------------------------------------------------ clips ---
$clips = Get-ChildItem -Path $ClipDir -Filter *.wav | Sort-Object Name
if (-not $clips) { Write-Error "No .wav clips found in $ClipDir"; exit 1 }

Write-Host ""
Write-Host "Reson8 live evaluation" -ForegroundColor Cyan
Write-Host "  endpoint : $endpoint"
Write-Host "  language : $lang"
Write-Host "  clips    : $($clips.Count)   passes: $($passes.Name -join ', ')   repeats: $Repeats"
Write-Host ""

function Invoke-Reson8 {
    param([string] $Path, [string[]] $Terms)

    $q = @("encoding=auto", "language=$([uri]::EscapeDataString($lang))")
    if ($Terms.Count -gt 0) {
        # Comma is the delimiter, so strip embedded commas exactly the way
        # Reson8Transcriber.BuildPhrases does.
        $clean = $Terms | ForEach-Object { ($_ -replace ',', ' ').Trim() } | Where-Object { $_ }
        $q += "phrases=$([uri]::EscapeDataString(($clean -join ',')))"
    }
    $url = $endpoint + '?' + ($q -join '&')

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $resp = Invoke-RestMethod -Method Post -Uri $url `
            -Headers @{ Authorization = "ApiKey $key" } `
            -ContentType 'application/octet-stream' `
            -InFile $Path
        $sw.Stop()
        return [pscustomobject]@{ Text = $resp.text; Ms = $sw.ElapsedMilliseconds; Error = $null }
    }
    catch {
        $sw.Stop()
        $msg = $_.Exception.Message
        # RFC 7807 problem+json: the lowercase `code` is what separates a bad
        # param from an entitlement/credit failure, so surface it.
        try {
            $r = $_.Exception.Response
            if ($r) {
                $sr = New-Object System.IO.StreamReader($r.GetResponseStream())
                $bodyText = $sr.ReadToEnd()
                if ($bodyText) {
                    $p = $bodyText | ConvertFrom-Json
                    $bits = @()
                    if ($p.code)   { $bits += "[$($p.code)]" }
                    if ($p.detail) { $bits += $p.detail } elseif ($p.title) { $bits += $p.title }
                    if ($bits.Count -gt 0) { $msg = $bits -join ' ' }
                }
            }
        } catch { }
        return [pscustomobject]@{ Text = $null; Ms = $sw.ElapsedMilliseconds; Error = $msg }
    }
}

$rows = @()
foreach ($clip in $clips) {
    Write-Host ("-" * 78)
    Write-Host $clip.BaseName -ForegroundColor White
    foreach ($pass in $passes) {
        for ($i = 1; $i -le $Repeats; $i++) {
            $r = Invoke-Reson8 -Path $clip.FullName -Terms $pass.Terms
            $tag = "{0,-12}" -f $pass.Name
            if ($r.Error) {
                Write-Host ("  {0} ERROR  {1}" -f $tag, $r.Error) -ForegroundColor Red
            } else {
                Write-Host ("  {0} {1,6} ms  {2}" -f $tag, $r.Ms, $r.Text)
            }
            $rows += [pscustomobject]@{
                Clip = $clip.BaseName; Pass = $pass.Name; Run = $i
                Ms = $r.Ms; Text = $r.Text; Error = $r.Error
            }
        }
    }
}

Write-Host ("-" * 78)
$outDir = Join-Path $here 'results'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$stamp = Get-Date -Format 'yyyy-MM-dd-HHmm'
$outFile = Join-Path $outDir "reson8-$stamp.csv"
$rows | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $outFile

$ok = @($rows | Where-Object { -not $_.Error })
if ($ok.Count -gt 0) {
    $med = ($ok.Ms | Sort-Object)[[int]($ok.Count / 2)]
    Write-Host ("median: {0} ms over {1} successful call(s)" -f $med, $ok.Count)
}
$bad = @($rows | Where-Object { $_.Error })
if ($bad.Count -gt 0) {
    Write-Host ("{0} call(s) failed" -f $bad.Count) -ForegroundColor Red
}
Write-Host "saved: $outFile"
Write-Host ""
