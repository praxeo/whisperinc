#requires -Version 5.1
<#
  _hotwords_ab.ps1 - Cohere vs Parakeet hotwords A/B over CrispASR server mode.

  PURPOSE
    Empirically test whether the Cohere Transcribe (GGUF AED) backend under
    CrispASR honors contextual biasing (the `hotwords` request field), using the
    Parakeet TDT backend as a POSITIVE CONTROL (CTC-WS shallow fusion - known to
    honor hotwords). If biasing moves Parakeet but leaves Cohere token-identical,
    that isolates "Cohere unsupported" from "flag broken".

  HOW IT TALKS TO CRISPASR
    No app changes. Spawns `crispasr.exe --server` and POSTs to the
    OpenAI-compatible /v1/audio/transcriptions endpoint - exactly mirroring
    WhisperInk's CrispAsrServerTranscriber.cs (spawn -> /health -> multipart
    POST with language/hotwords/beam_size/response_format/file -> parse .text).

    Source analysis (see plans/cohere-biasing-findings.md) established that both
    `hotwords` AND `beam_size` are PER-REQUEST server form fields
    (crispasr_server.cpp lines 780/917 and 792/924), so a single server per cell
    covers the full beam x hotwords matrix. To eliminate any per-request state
    leakage this harness still relaunches a fresh server for EVERY cell
    (backend x beam x hotwords) and captures that server's stderr to its own log
    - i.e. it generalizes the sprint's "Server A (no hotwords) vs Server B (with
    hotwords)" isolation to every condition.

  USAGE
    .\_hotwords_ab.ps1                 # full matrix, GPU backend "auto"
    .\_hotwords_ab.ps1 -GpuBackend cpu # force CPU (held constant across cells)
    .\_hotwords_ab.ps1 -ParakeetOnly   # just the positive control
    .\_hotwords_ab.ps1 -CohereOnly     # just the backend under test

  PRECONDITION
    Real recordings (the user's own voice) must exist under .\clips\ per the
    manifest in RECORD_THESE.md. The script STOPS with instructions if they are
    missing - it never substitutes TTS (OOV failures are speaker/acoustic
    specific; TTS would invalidate the test).
#>

[CmdletBinding()]
param(
    [string]$GpuBackend = "auto",                 # held constant across ALL cells
    [int]$BasePort      = 8200,                    # avoids app presets (8103/8105-8108/8766)
    [string]$ClipsDir   = (Join-Path $PSScriptRoot "clips"),
    [string]$OutDir     = (Join-Path $PSScriptRoot "results"),
    [double]$HotwordsBoost = 0,                    # 0 => omit field (server default 2.0); >0 => send explicit
    [switch]$ParakeetOnly,
    [switch]$CohereOnly
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Net.Http | Out-Null

# Verbose backend logging so we can scan stderr for any hotword-related line
# (Deliverable 3.2). Inherited by child crispasr.exe processes. NOTE: this is
# why we do NOT pass -np here, unlike the app (transcript text is identical
# either way; -np only suppresses console diagnostics we want to capture).
$env:CRISPASR_VERBOSE = "1"

# --------------------------------------------------------------------------- #
#  Fixed configuration                                                        #
# --------------------------------------------------------------------------- #
$ExeDir = Join-Path $env:APPDATA ".WhisperInk\cohere-gguf"
$Exe    = Join-Path $ExeDir "crispasr.exe"
$Threads = [Math]::Min(8, [Environment]::ProcessorCount)  # matches the app's cap

# Target lexicon. Order matters only for display. The "on" condition sends all
# of these as the comma-separated hotwords field.
$Lexicon = @("hematochezia", "ureterolithiasis", "biliary colic", "ureteral colic")

# Clip manifest: each term-bearing clip names its single expected term.
# `neutral` carries none of the lexicon terms (spurious-injection probe) and is
# optional. See RECORD_THESE.md for the exact sentences to read.
$Clips = @(
    [pscustomobject]@{ File = "hematochezia_1.wav";   Term = "hematochezia";     Control = $false; Required = $true  }
    [pscustomobject]@{ File = "hematochezia_2.wav";   Term = "hematochezia";     Control = $false; Required = $true  }
    [pscustomobject]@{ File = "ureterolithiasis.wav"; Term = "ureterolithiasis"; Control = $true;  Required = $true  }
    [pscustomobject]@{ File = "biliary_colic.wav";    Term = "biliary colic";    Control = $true;  Required = $true  }
    [pscustomobject]@{ File = "ureteral_colic.wav";   Term = "ureteral colic";   Control = $true;  Required = $true  }
    [pscustomobject]@{ File = "neutral.wav";          Term = "";                 Control = $true;  Required = $false }
)

# Backends. Both GGUFs are present in $ExeDir. Cohere needs an explicit
# --backend (metadata lacks the auto-detect marker); parakeet is passed
# explicitly too for determinism.
$AllBackends = @(
    [pscustomobject]@{ Name = "cohere";   Model = "cohere-transcribe-q6_k.gguf";    Hint = "cohere"   }
    [pscustomobject]@{ Name = "parakeet"; Model = "parakeet-tdt-0.6b-v3-q4_k.gguf"; Hint = "parakeet" }  # positive control
)
$Backends = $AllBackends
if ($ParakeetOnly) { $Backends = @($AllBackends | Where-Object Name -eq "parakeet") }
if ($CohereOnly)   { $Backends = @($AllBackends | Where-Object Name -eq "cohere")   }

$Beams    = @(1, 5)
$HotConds = @(
    [pscustomobject]@{ Name = "off"; Send = $false }
    [pscustomobject]@{ Name = "on";  Send = $true  }
)

$Hotwords = ($Lexicon -join ",")

# --------------------------------------------------------------------------- #
#  Helpers                                                                     #
# --------------------------------------------------------------------------- #

function Normalize-Text {
    param([string]$s)
    if (-not $s) { return "" }
    $t = $s.ToLowerInvariant()
    $t = ($t -replace "[^a-z0-9]+", " ").Trim()
    return " $t "   # pad so substring search is word-boundary safe
}

function Test-TermPresent {
    param([string]$Text, [string]$Term)
    if (-not $Term) { return $false }
    $hay = Normalize-Text $Text
    $needle = (Normalize-Text $Term)   # already padded both sides
    return $hay.Contains($needle)
}

function Wait-Health {
    param([int]$Port, [int]$TimeoutSec = 120, [System.Diagnostics.Process]$Proc)
    $url = "http://127.0.0.1:$Port/health"
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if ($Proc -and $Proc.HasExited) { return $false }
        try {
            $r = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 2
            if ($r.StatusCode -eq 200) { return $true }
        } catch { }
        Start-Sleep -Milliseconds 300
    }
    return $false
}

function Start-CrispServer {
    param([string]$Model, [string]$Hint, [int]$Beam, [int]$Port, [string]$ErrLog, [string]$OutLog)
    $modelPath = Join-Path $ExeDir $Model
    if (-not (Test-Path $modelPath)) { throw "model not found: $modelPath" }

    $srvArgs = @(
        "--server",
        "--host", "127.0.0.1",
        "--port", "$Port",
        "-m", $modelPath,
        "-t", "$Threads",
        "-bs", "$Beam",                 # set beam at launch too (belt and suspenders)
        "--backend", $Hint
    )
    if ($GpuBackend -eq "cpu") {
        $srvArgs += "-ng"               # mirror the app: force CPU by disabling GPU
    } elseif ($GpuBackend -and $GpuBackend -ne "auto") {
        $srvArgs += @("--gpu-backend", $GpuBackend)
    }

    $p = Start-Process -FilePath $Exe -ArgumentList $srvArgs -WorkingDirectory $ExeDir `
        -NoNewWindow -PassThru -RedirectStandardError $ErrLog -RedirectStandardOutput $OutLog
    return $p
}

function Stop-CrispServer {
    param([System.Diagnostics.Process]$Proc)
    if (-not $Proc) { return }
    try { if (-not $Proc.HasExited) { & taskkill /PID $Proc.Id /T /F 2>$null | Out-Null } } catch { }
    try { $Proc.Dispose() } catch { }
}

function Invoke-Transcribe {
    param([int]$Port, [string]$WavPath, [string]$Language, [int]$Beam, [string]$SendHotwords)
    $url = "http://127.0.0.1:$Port/v1/audio/transcriptions"
    $client = [System.Net.Http.HttpClient]::new()
    $client.Timeout = [TimeSpan]::FromSeconds(120)
    try {
        $form = [System.Net.Http.MultipartFormDataContent]::new()
        $form.Add([System.Net.Http.StringContent]::new($Language), "language")
        $form.Add([System.Net.Http.StringContent]::new([string]$Beam), "beam_size")
        $form.Add([System.Net.Http.StringContent]::new("json"), "response_format")
        if ($SendHotwords) {
            $form.Add([System.Net.Http.StringContent]::new($SendHotwords), "hotwords")
            if ($HotwordsBoost -gt 0) {
                $form.Add([System.Net.Http.StringContent]::new([string]$HotwordsBoost), "hotwords_boost")
            }
        }
        $bytes = [System.IO.File]::ReadAllBytes($WavPath)
        $fileContent = [System.Net.Http.ByteArrayContent]::new($bytes)
        $fileContent.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse("audio/wav")
        $form.Add($fileContent, "file", "audio.wav")

        $resp = $client.PostAsync($url, $form).GetAwaiter().GetResult()
        $body = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if (-not $resp.IsSuccessStatusCode) {
            return [pscustomobject]@{ Ok = $false; Text = ""; Status = [int]$resp.StatusCode; Raw = $body }
        }
        $text = ""
        try { $text = ($body | ConvertFrom-Json).text } catch { }
        return [pscustomobject]@{ Ok = $true; Text = ($text).Trim(); Status = 200; Raw = $body }
    } finally {
        $client.Dispose()
    }
}

# --------------------------------------------------------------------------- #
#  Preconditions / recording gate                                             #
# --------------------------------------------------------------------------- #
if (-not (Test-Path $Exe)) { throw "crispasr.exe not found at $Exe" }

$presentClips = @($Clips | Where-Object { Test-Path (Join-Path $ClipsDir $_.File) })
$missingRequired = @($Clips | Where-Object { $_.Required -and -not (Test-Path (Join-Path $ClipsDir $_.File)) })

if ($presentClips.Count -eq 0 -or $missingRequired.Count -gt 0) {
    Write-Host ""
    Write-Host "STOP: required recordings are missing." -ForegroundColor Yellow
    Write-Host "This test needs REAL clips in your own voice - do not use TTS." -ForegroundColor Yellow
    Write-Host "Expected under: $ClipsDir" -ForegroundColor Yellow
    Write-Host ""
    foreach ($c in $Clips) {
        $exists = Test-Path (Join-Path $ClipsDir $c.File)
        $tag = if ($exists) { "[ok]     " } elseif ($c.Required) { "[MISSING]" } else { "[opt]    " }
        Write-Host ("  {0} {1,-22} term: {2}" -f $tag, $c.File, $(if ($c.Term) { $c.Term } else { "(none)" }))
    }
    Write-Host ""
    Write-Host "See RECORD_THESE.md (same folder) for the exact sentences to read." -ForegroundColor Yellow
    exit 2
}

$stamp   = Get-Date -Format "yyyy-MM-dd-HHmmss"
$runDir  = Join-Path $OutDir $stamp
New-Item -ItemType Directory -Force -Path $runDir | Out-Null

Write-Host "Hotwords A/B run $stamp"
Write-Host "  exe        : $Exe"
Write-Host "  gpu-backend: $GpuBackend (constant)"
Write-Host "  threads    : $Threads"
Write-Host "  clips      : $($presentClips.Count) present"
Write-Host "  hotwords   : $Hotwords"
Write-Host "  out        : $runDir"
Write-Host ""

# --------------------------------------------------------------------------- #
#  Run the matrix: backend x beam x hotwords, fresh server per cell            #
# --------------------------------------------------------------------------- #
$rows = New-Object System.Collections.Generic.List[object]
$logScan = New-Object System.Collections.Generic.List[object]
$cellIndex = 0

foreach ($be in $Backends) {
  foreach ($beam in $Beams) {
    foreach ($hc in $HotConds) {
      $cellIndex++
      $port = $BasePort + $cellIndex
      $cellId = "{0}_beam{1}_hot{2}" -f $be.Name, $beam, $hc.Name
      $errLog = Join-Path $runDir "$cellId.stderr.log"
      $outLog = Join-Path $runDir "$cellId.stdout.log"
      Write-Host ("[cell {0}] {1}  port {2}" -f $cellIndex, $cellId, $port)

      $proc = $null
      try {
        $proc = Start-CrispServer -Model $be.Model -Hint $be.Hint -Beam $beam -Port $port -ErrLog $errLog -OutLog $outLog
        if (-not (Wait-Health -Port $port -TimeoutSec 120 -Proc $proc)) {
          Write-Host "    server failed to become healthy - skipping cell" -ForegroundColor Red
          continue
        }

        # Discard one cold/warm priming request (CrispASR auto-warms at start,
        # but we follow the spawn/discard/measure pattern anyway).
        $first = $presentClips[0]
        $null = Invoke-Transcribe -Port $port -WavPath (Join-Path $ClipsDir $first.File) `
                  -Language "en" -Beam $beam -SendHotwords $(if ($hc.Send) { $Hotwords } else { "" })

        foreach ($c in $presentClips) {
          $wav = Join-Path $ClipsDir $c.File
          $res = Invoke-Transcribe -Port $port -WavPath $wav -Language "en" -Beam $beam `
                    -SendHotwords $(if ($hc.Send) { $Hotwords } else { "" })
          $hit = Test-TermPresent -Text $res.Text -Term $c.Term

          # Spurious injection: any OTHER lexicon term showing up in this clip.
          $spurious = @()
          foreach ($u in $Lexicon) {
            if ($u -ne $c.Term -and (Test-TermPresent -Text $res.Text -Term $u)) { $spurious += $u }
          }

          $rows.Add([pscustomobject]@{
            Backend  = $be.Name
            Beam     = $beam
            Hotwords = $hc.Name
            Clip     = $c.File
            Term     = $c.Term
            Control  = $c.Control
            Hit      = [bool]$hit
            Spurious = ($spurious -join "|")
            Status   = $res.Status
            Text     = $res.Text
          })
          $mark = if ($hit) { "HIT " } elseif (-not $c.Term) { "n/a " } else { "miss" }
          Write-Host ("    {0} {1,-22} {2}" -f $mark, $c.File, $res.Text)
        }

        # Scan this cell's stderr for any hotword-related line.
        if (Test-Path $errLog) {
          $hwLines = @(Select-String -Path $errLog -Pattern "hotword" -SimpleMatch -ErrorAction SilentlyContinue |
                       ForEach-Object { $_.Line.Trim() })
          $logScan.Add([pscustomobject]@{ Cell = $cellId; HotwordLines = ($hwLines -join " || ") ; Count = $hwLines.Count })
        }
      }
      finally {
        Stop-CrispServer -Proc $proc
        Start-Sleep -Milliseconds 400   # let the port settle before next bind
      }
    }
  }
}

# --------------------------------------------------------------------------- #
#  Persist raw rows                                                            #
# --------------------------------------------------------------------------- #
$csvPath  = Join-Path $runDir "rows.csv"
$jsonPath = Join-Path $runDir "rows.json"
$rows | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8
$rows | ConvertTo-Json -Depth 5 | Out-File -FilePath $jsonPath -Encoding UTF8

# --------------------------------------------------------------------------- #
#  Score + summarize                                                          #
# --------------------------------------------------------------------------- #
function HitRate {
    param($subset)
    $den = @($subset).Count
    if ($den -eq 0) { return "n/a" }
    $num = @($subset | Where-Object Hit).Count
    return ("{0}/{1}" -f $num, $den)
}

$summary = New-Object System.Collections.Generic.List[object]
foreach ($be in ($Backends | Select-Object -ExpandProperty Name)) {
  foreach ($beam in $Beams) {
    $off = @($rows | Where-Object { $_.Backend -eq $be -and $_.Beam -eq $beam -and $_.Hotwords -eq "off" })
    $on  = @($rows | Where-Object { $_.Backend -eq $be -and $_.Beam -eq $beam -and $_.Hotwords -eq "on"  })
    if ($off.Count -eq 0 -and $on.Count -eq 0) { continue }

    $offTarget = @($off | Where-Object { -not $_.Control })
    $onTarget  = @($on  | Where-Object { -not $_.Control })
    $offCtl    = @($off | Where-Object { $_.Control -and $_.Term })
    $onCtl     = @($on  | Where-Object { $_.Control -and $_.Term })

    # text changes off->on, matched per clip
    $changed = 0; $compared = 0
    foreach ($r in $on) {
      $match = $off | Where-Object { $_.Clip -eq $r.Clip } | Select-Object -First 1
      if ($match) { $compared++; if ((Normalize-Text $match.Text) -ne (Normalize-Text $r.Text)) { $changed++ } }
    }
    $spuriousOn = @($on | Where-Object { $_.Spurious }).Count

    $summary.Add([pscustomobject]@{
      Backend          = $be
      Beam             = $beam
      TargetHit_off    = (HitRate $offTarget)
      TargetHit_on     = (HitRate $onTarget)
      ControlHit_off   = (HitRate $offCtl)
      ControlHit_on    = (HitRate $onCtl)
      TextChanged      = ("{0}/{1}" -f $changed, $compared)
      SpuriousInj_on   = $spuriousOn
    })
  }
}

Write-Host ""
Write-Host "==================== SUMMARY ===================="
$summary | Format-Table -AutoSize | Out-String -Width 200 | Write-Host

Write-Host "Log scan (hotword-mentioning stderr lines per cell):"
$logScan | Format-Table -AutoSize | Out-String -Width 200 | Write-Host

# --------------------------------------------------------------------------- #
#  Ready-to-paste markdown block                                              #
# --------------------------------------------------------------------------- #
$md = New-Object System.Collections.Generic.List[string]
$md.Add("### Empirical A/B results ($stamp)")
$md.Add("")
$md.Add("GPU backend: ``$GpuBackend`` (constant). Threads: $Threads. Hotwords sent: ``$Hotwords``.")
$md.Add("")
$md.Add("| Backend | Beam | Target hit off | Target hit on | Control hit off | Control hit on | Text changed off->on | Spurious inj (on) |")
$md.Add("|---|---|---|---|---|---|---|---|")
foreach ($s in $summary) {
  $md.Add("| $($s.Backend) | $($s.Beam) | $($s.TargetHit_off) | $($s.TargetHit_on) | $($s.ControlHit_off) | $($s.ControlHit_on) | $($s.TextChanged) | $($s.SpuriousInj_on) |")
}
$md.Add("")
$md.Add("Interpretation key: for Cohere, ``Text changed off->on`` = 0/N across both beams is the")
$md.Add("token-for-token no-op signal. For Parakeet (positive control), a non-zero target-hit")
$md.Add("lift and/or text-changed count proves the harness exercises real biasing.")
$md.Add("")
$md.Add("Per-cell hotword log lines:")
foreach ($l in $logScan) { $md.Add("- ``$($l.Cell)``: $(if ($l.Count -gt 0) { $l.HotwordLines } else { '(none)' })") }
$mdPath = Join-Path $runDir "summary.md"
$md -join "`r`n" | Out-File -FilePath $mdPath -Encoding UTF8

Write-Host ""
Write-Host "Artifacts:"
Write-Host "  rows.csv   : $csvPath"
Write-Host "  rows.json  : $jsonPath"
Write-Host "  summary.md : $mdPath   <- paste into plans/cohere-biasing-findings.md"
Write-Host ""
Write-Host "Done."
