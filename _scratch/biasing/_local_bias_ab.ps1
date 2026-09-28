#requires -Version 7
<#
  _local_bias_ab.ps1 - local biasing A/B on the six clips in .\clips\.

  For each backend: spawn its own crispasr.exe --server on an unused port,
  then POST every clip twice without and twice with hotwords, mirroring what
  CrispAsrServerTranscriber sends (language=en, response_format=json,
  hotwords = the CURRENT shared Context Bias list from config.json,
  comma-joined, and the preset's LocalExtraParams, as Fields below). A
  transcript holding four or more of the list's terms is marked LIST?: the
  model reciting its prompt. Never touches the app's servers or any other
  crispasr process; each server this starts is stopped by PID. Server output
  goes to .\results\ (git-ignored).

  Produced the 2026-09-23 table in CLAUDE.md: Qwen3-ASR 3/3 with no
  collateral; Granite 3/3 but it rewrote a correct "ureteral colic". And
  the 2026-09-24 Orukeet and Parakeet Ultra rows, against their base
  model, Parakeet TDT v3, all three in one back-to-back run. And the
  2026-09-24 Voxtral rows: Mini 3B and 4B Realtime against Qwen3, plus
  the longer takes _join_clips.ps1 builds, and Qwen3's 60 s pieces.

  A run with Backend = '' passes no --backend, as a model added from the
  model folder does (crispasr detects it from the file). Args are extra
  server flags, as the preset would pass them (Parakeet RNNT's punctuation).

  To A/B a CrispASR release before deploying it, run everything twice with
  -Csv, once as is (the deployed exe) and once with -Exe pointing at the
  release's unzipped crispasr.exe, then compare with _ab_compare.ps1. A
  server that fails to start, or a request that fails, is recorded as an
  <error: ...> row and the run moves on.

  USAGE    pwsh .\_local_bias_ab.ps1                    # every run
           pwsh .\_local_bias_ab.ps1 -Only orukeet,tdt  # runs whose name contains one of these
           pwsh .\_local_bias_ab.ps1 -Extra .\joined     # also these WAVs (files or folders), after the six clips
           pwsh .\_local_bias_ab.ps1 -Drop epigastric    # leave these terms out of the list
           pwsh .\_local_bias_ab.ps1 -Fields chunk_seconds=30   # extra form fields for every run (k=v;k=v)
           pwsh .\_local_bias_ab.ps1 -Exe <stage>\crispasr.exe -Csv new.csv -Label new-
                                                         # another binary; rows to a CSV; server logs as new-*.txt
  NEEDS    the user's own recordings in .\clips\ (see RECORD_THESE.md), and
           the listed GGUFs in %APPDATA%\.WhisperInk\cohere-gguf\
#>
param([string[]]$Only, [string[]]$Extra, [string[]]$Drop, [string]$Fields, [string]$Exe, [string]$Csv, [string]$Label)
$ErrorActionPreference = 'Stop'
$dir   = Join-Path $env:APPDATA '.WhisperInk\cohere-gguf'
$exe   = if ($Exe) { (Resolve-Path $Exe).Path } else { Join-Path $dir 'crispasr.exe' }
if (-not (Test-Path $exe)) { throw "no crispasr.exe at $exe" }
$clips = Get-ChildItem (Join-Path $PSScriptRoot 'clips\*.wav') | Sort-Object Name
if (-not $clips) { throw "no clips in $(Join-Path $PSScriptRoot 'clips') - see RECORD_THESE.md" }
# `pwsh -File` hands "a,b" over as one string, so these are split here too.
$Extra = @($Extra | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
foreach ($e in $Extra) {
  $clips = @($clips) + @(if (Test-Path $e -PathType Container) { Get-ChildItem (Join-Path $e '*.wav') | Sort-Object Name } else { Get-Item $e })
}
$Drop  = @($Drop | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$cfg   = Get-Content (Join-Path $env:APPDATA '.WhisperInk\config.json') -Raw | ConvertFrom-Json
$terms = @($cfg.ContextBiasTerms | Where-Object { $_ -and $_ -notin $Drop })
$hot   = $terms -join ','
$out   = Join-Path $PSScriptRoot 'results'
New-Item -ItemType Directory -Force $out | Out-Null
"exe: $exe"
"hotwords ($($terms.Count) terms): $hot"
$rows = [System.Collections.Generic.List[object]]::new()

$runs = @(
  @{ Name = 'granite 4.1 2b';      Model = 'granite-speech-4.1-2b-q4_k.gguf';      Backend = 'granite';    Port = 18207 },
  @{ Name = 'granite 4.1 2b-plus'; Model = 'granite-speech-4.1-2b-plus-q4_k.gguf'; Backend = 'granite';    Port = 18208 },
  @{ Name = 'cohere q6_k';         Model = 'cohere-transcribe-q6_k.gguf';          Backend = 'cohere';     Port = 18209 },
  @{ Name = 'parakeet rnnt 1.1b';  Model = 'parakeet-rnnt-1.1b-q4_k.gguf';         Backend = '';           Port = 18210; Args = @('--punc-model', 'fullstop') },
  @{ Name = 'qwen3-asr 1.7b';      Model = 'qwen3-asr-1.7b-q4_k.gguf';             Backend = 'qwen3-1.7b'; Port = 18212; Fields = @{ chunk_seconds = '60' } },
  @{ Name = 'parakeet tdt 0.6b v3'; Model = 'parakeet-tdt-0.6b-v3-q4_k.gguf';      Backend = '';           Port = 18213 },
  @{ Name = 'orukeet';             Model = 'orukeet-q4_k.gguf';                    Backend = '';           Port = 18214 },
  @{ Name = 'parakeet ultra';      Model = 'parakeet-ultra-q4_k.gguf';             Backend = '';           Port = 18215 },
  @{ Name = 'voxtral mini 3b';     Model = 'voxtral-mini-3b-2507-q4_k.gguf';       Backend = 'voxtral';    Port = 18216 },
  @{ Name = 'voxtral 4b realtime'; Model = 'voxtral-mini-4b-realtime-q4_k.gguf';   Backend = 'voxtral4b';  Port = 18217 }
)
# `pwsh -File` hands "orukeet,tdt" over as one string, so split it here.
$Only = @($Only | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
if ($Only) { $runs = @($runs | Where-Object { $n = $_.Name; @($Only | Where-Object { $n -like "*$_*" }).Count -gt 0 }) }
$override = @{}
foreach ($kv in @($Fields -split ';' | Where-Object { $_ })) { $k, $v = $kv -split '=', 2; $override[$k.Trim()] = $v.Trim() }

foreach ($run in $runs) {
  $model = Join-Path $dir $run.Model
  if (-not (Test-Path $model)) { "`n=== $($run.Name): $($run.Model) not on disk, skipped"; continue }
  $argList = @('--server','--host','127.0.0.1','--port',"$($run.Port)",'-m',$model,'-t','8','-np','--gpu-backend','cuda')
  if ($run.Backend) { $argList += @('--backend', $run.Backend) }
  if ($run.Args) { $argList += $run.Args }
  $tag = $Label + ($run.Model -replace '\.gguf$','')
  $backend = ''
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
    $backend = (Select-String -Path (Join-Path $out "$tag.err.txt"), (Join-Path $out "$tag.out.txt") -Pattern "backend '([^']+)' loaded" |
                Select-Object -First 1).Matches.Groups[1].Value
    $sent = @{}
    if ($run.Fields) { foreach ($e in $run.Fields.GetEnumerator()) { $sent[$e.Key] = $e.Value } }
    foreach ($e in $override.GetEnumerator()) { $sent[$e.Key] = $e.Value }
    $shown = if ($sent.Count) { ", $(($sent.GetEnumerator() | Sort-Object Key | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ' ')" } else { '' }
    "`n=== $($run.Name)  ($($run.Model), backend $backend$shown, ready in $([int]$sw.Elapsed.TotalSeconds) s)"
    :clips foreach ($clip in $clips) {
      foreach ($bias in $false, $true) {
        foreach ($rep in 1, 2) {
          $form = @{ language = 'en'; response_format = 'json'; file = Get-Item $clip.FullName }
          if ($bias) { $form.hotwords = $hot }
          foreach ($e in $sent.GetEnumerator()) { $form[$e.Key] = $e.Value }
          $mode = if ($bias) { 'BIAS' } else { 'none' }
          $t = [Diagnostics.Stopwatch]::StartNew()
          try {
            $r = Invoke-RestMethod "http://127.0.0.1:$($run.Port)/v1/audio/transcriptions" -Method Post -Form $form -TimeoutSec 300
            $text = "$($r.text)".Trim()
          } catch { $text = "<error: $($_.Exception.Message)>" }
          $recited = @($terms | Where-Object { $text -match ('(?i)(?<!\w)' + [regex]::Escape($_) + '(?!\w)') }).Count -ge 4
          "{0,-17} {1,-8} rep{2} {3,5} ms  {4}{5}" -f $clip.BaseName, $mode, $rep, $t.ElapsedMilliseconds, $(if ($recited) { 'LIST? ' } else { '' }), $text
          $rows.Add([pscustomobject]@{ Run = $run.Name; Model = $run.Model; Backend = $backend; Clip = $clip.BaseName; Bias = $mode
                                       Rep = $rep; Ms = $t.ElapsedMilliseconds; Recited = $recited; Text = $text })
          if ($p.HasExited) { "server died (exit $($p.ExitCode)) - see results\$tag.err.txt"; break clips }
        }
      }
    }
  } catch {
    "`n=== $($run.Name): FAILED - $($_.Exception.Message)"
    $rows.Add([pscustomobject]@{ Run = $run.Name; Model = $run.Model; Backend = $backend; Clip = '(server)'; Bias = ''
                                 Rep = 0; Ms = 0; Recited = $false; Text = "<error: $($_.Exception.Message)>" })
  } finally {
    if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
  }
}
if ($Csv) { $rows | Export-Csv -LiteralPath $Csv -NoTypeInformation -Encoding utf8; "`nwrote $($rows.Count) rows to $Csv" }
