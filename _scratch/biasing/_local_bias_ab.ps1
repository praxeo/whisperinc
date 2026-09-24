#requires -Version 7
<#
  _local_bias_ab.ps1 - local biasing A/B on the six clips in .\clips\.

  For each backend: spawn its own crispasr.exe --server on an unused port,
  then POST every clip twice without and twice with hotwords, mirroring what
  CrispAsrServerTranscriber sends (language=en, response_format=json, and
  hotwords = the CURRENT shared Context Bias list from config.json,
  comma-joined). Never touches the app's servers or any other crispasr
  process; each server this starts is stopped by PID. Server output goes
  to .\results\ (git-ignored).

  Produced the 2026-09-23 table in CLAUDE.md: Qwen3-ASR 3/3 with no
  collateral; Granite 3/3 but it rewrote a correct "ureteral colic".

  USAGE    pwsh .\_local_bias_ab.ps1
  NEEDS    the user's own recordings in .\clips\ (see RECORD_THESE.md), and
           the listed GGUFs in %APPDATA%\.WhisperInk\cohere-gguf\
#>
$ErrorActionPreference = 'Stop'
$dir   = Join-Path $env:APPDATA '.WhisperInk\cohere-gguf'
$exe   = Join-Path $dir 'crispasr.exe'
$clips = Get-ChildItem (Join-Path $PSScriptRoot 'clips\*.wav') | Sort-Object Name
if (-not $clips) { throw "no clips in $(Join-Path $PSScriptRoot 'clips') - see RECORD_THESE.md" }
$cfg   = Get-Content (Join-Path $env:APPDATA '.WhisperInk\config.json') -Raw | ConvertFrom-Json
$hot   = ($cfg.ContextBiasTerms | Where-Object { $_ }) -join ','
$out   = Join-Path $PSScriptRoot 'results'
New-Item -ItemType Directory -Force $out | Out-Null
"hotwords ($(@($cfg.ContextBiasTerms).Count) terms): $hot"

$runs = @(
  @{ Name = 'granite 4.1 2b';      Model = 'granite-speech-4.1-2b-q4_k.gguf';      Backend = 'granite';    Port = 18207 },
  @{ Name = 'granite 4.1 2b-plus'; Model = 'granite-speech-4.1-2b-plus-q4_k.gguf'; Backend = 'granite';    Port = 18208 },
  @{ Name = 'qwen3-asr 1.7b';      Model = 'qwen3-asr-1.7b-q4_k.gguf';             Backend = 'qwen3-1.7b'; Port = 18212 }
)

foreach ($run in $runs) {
  $model = Join-Path $dir $run.Model
  if (-not (Test-Path $model)) { "`n=== $($run.Name): $($run.Model) not on disk, skipped"; continue }
  $argList = @('--server','--host','127.0.0.1','--port',"$($run.Port)",'-m',$model,'-t','8','-np','--backend',$run.Backend,'--gpu-backend','cuda')
  $tag = $run.Model -replace '\.gguf$',''
  $p = Start-Process -FilePath $exe -ArgumentList $argList -PassThru -WindowStyle Hidden `
         -RedirectStandardOutput (Join-Path $out "$tag.out.txt") -RedirectStandardError (Join-Path $out "$tag.err.txt")
  try {
    $ok = $false
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt 180) {
      if ($p.HasExited) { throw "server exited early ($($p.ExitCode)) - see results\$tag.err.txt" }
      try { Invoke-RestMethod "http://127.0.0.1:$($run.Port)/health" -TimeoutSec 2 | Out-Null; $ok = $true; break } catch { Start-Sleep -Milliseconds 500 }
    }
    if (-not $ok) { throw "no /health within 180 s" }
    "`n=== $($run.Name)  ($($run.Model), ready in $([int]$sw.Elapsed.TotalSeconds) s)"
    foreach ($clip in $clips) {
      foreach ($bias in $false, $true) {
        foreach ($rep in 1, 2) {
          $form = @{ language = 'en'; response_format = 'json'; file = Get-Item $clip.FullName }
          if ($bias) { $form.hotwords = $hot }
          $t = [Diagnostics.Stopwatch]::StartNew()
          $r = Invoke-RestMethod "http://127.0.0.1:$($run.Port)/v1/audio/transcriptions" -Method Post -Form $form -TimeoutSec 300
          "{0,-17} {1,-8} rep{2} {3,5} ms  {4}" -f $clip.BaseName, ($(if ($bias) { 'BIAS' } else { 'none' })), $rep, $t.ElapsedMilliseconds, $r.text.Trim()
        }
      }
    }
  } finally {
    if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
  }
}
