#requires -Version 5.1
<#
  _boost_sweep.ps1 - Parakeet HotwordsBoost sweep for the WhisperInk medical lexicon.

  WHY
    Parakeet (TDT/RNNT/CTC) is the only local CrispASR backend that does REAL
    token-level biasing (Aho-Corasick trie -> logit boost before argmax). The
    open question is the BOOST value: high enough to recover rare terms
    (hematochezia), low enough not to garble neighboring/control words. This
    sweeps boost on the user's real clinical clips to find that sweet spot.

  HOW
    hotwords + hotwords_boost are PER-REQUEST server form fields, so we spawn ONE
    crispasr server per backend (warm once) and vary the boost per request -
    much faster than re-spawning. Greedy (beam 1) - the app's default path, and
    the one the trie biases (the MAES beam variant is a no-op, but it's opt-in).

  CONDITIONS (per clip, per backend)
    off       - no hotwords (baseline)
    default   - hotwords on, no explicit boost (server default ~2.0)
    boost3/5/8/10 - hotwords on, explicit global boost
    perterm5  - per-term suffix "term^5.0" (boost only the hard words)
#>
[CmdletBinding()]
param(
    [string]$GpuBackend = "auto",
    [int]$BasePort      = 8210,
    [string]$ClipsDir   = (Join-Path $PSScriptRoot "clips"),
    [string]$OutDir     = (Join-Path $PSScriptRoot "results")
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Net.Http | Out-Null

$ExeDir  = Join-Path $env:APPDATA ".WhisperInk\cohere-gguf"
$Exe     = Join-Path $ExeDir "crispasr.exe"
$Threads = [Math]::Min(8, [Environment]::ProcessorCount)

$Lexicon         = @("hematochezia", "ureterolithiasis", "biliary colic", "ureteral colic")
$Hotwords        = ($Lexicon -join ",")
$HotwordsPerTerm = (($Lexicon | ForEach-Object { "$_^5.0" }) -join ",")

$Clips = @(
    [pscustomobject]@{ File = "hematochezia_1.wav";   Term = "hematochezia";     Control = $false }
    [pscustomobject]@{ File = "hematochezia_2.wav";   Term = "hematochezia";     Control = $false }
    [pscustomobject]@{ File = "ureterolithiasis.wav"; Term = "ureterolithiasis"; Control = $true  }
    [pscustomobject]@{ File = "biliary_colic.wav";    Term = "biliary colic";    Control = $true  }
    [pscustomobject]@{ File = "ureteral_colic.wav";   Term = "ureteral colic";   Control = $true  }
    [pscustomobject]@{ File = "neutral.wav";          Term = "";                 Control = $true  }
)

$Backends = @(
    [pscustomobject]@{ Name = "parakeet-tdt";  Model = "parakeet-tdt-0.6b-v3-q4_k.gguf"; Hint = "parakeet" }
    [pscustomobject]@{ Name = "parakeet-rnnt"; Model = "parakeet-rnnt-1.1b-q4_k.gguf";   Hint = "parakeet" }
)

$Conds = @(
    [pscustomobject]@{ Name = "off";      Hot = "";               Boost = 0  }
    [pscustomobject]@{ Name = "default";  Hot = $Hotwords;        Boost = 0  }
    [pscustomobject]@{ Name = "boost3";   Hot = $Hotwords;        Boost = 3  }
    [pscustomobject]@{ Name = "boost5";   Hot = $Hotwords;        Boost = 5  }
    [pscustomobject]@{ Name = "boost8";   Hot = $Hotwords;        Boost = 8  }
    [pscustomobject]@{ Name = "boost10";  Hot = $Hotwords;        Boost = 10 }
    [pscustomobject]@{ Name = "perterm5"; Hot = $HotwordsPerTerm; Boost = 0  }
)

function Normalize-Text {
    param([string]$s)
    if (-not $s) { return "" }
    $t = $s.ToLowerInvariant()
    $t = ($t -replace "[^a-z0-9]+", " ").Trim()
    return " $t "
}
function Test-TermPresent {
    param([string]$Text, [string]$Term)
    if (-not $Term) { return $false }
    return (Normalize-Text $Text).Contains((Normalize-Text $Term))
}
function Wait-Health {
    param([int]$Port, [int]$TimeoutSec = 120, [System.Diagnostics.Process]$Proc)
    $url = "http://127.0.0.1:$Port/health"
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if ($Proc -and $Proc.HasExited) { return $false }
        try { if ((Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 2).StatusCode -eq 200) { return $true } } catch { }
        Start-Sleep -Milliseconds 300
    }
    return $false
}
function Start-CrispServer {
    param([string]$Model, [string]$Hint, [int]$Port, [string]$ErrLog, [string]$OutLog)
    $modelPath = Join-Path $ExeDir $Model
    if (-not (Test-Path $modelPath)) { throw "model not found: $modelPath" }
    $srvArgs = @("--server", "--host", "127.0.0.1", "--port", "$Port", "-m", $modelPath, "-t", "$Threads", "-bs", "1", "--backend", $Hint)
    if ($GpuBackend -eq "cpu") { $srvArgs += "-ng" }
    elseif ($GpuBackend -and $GpuBackend -ne "auto") { $srvArgs += @("--gpu-backend", $GpuBackend) }
    return Start-Process -FilePath $Exe -ArgumentList $srvArgs -WorkingDirectory $ExeDir -NoNewWindow -PassThru -RedirectStandardError $ErrLog -RedirectStandardOutput $OutLog
}
function Stop-CrispServer {
    param([System.Diagnostics.Process]$Proc)
    if (-not $Proc) { return }
    try { if (-not $Proc.HasExited) { & taskkill /PID $Proc.Id /T /F 2>$null | Out-Null } } catch { }
    try { $Proc.Dispose() } catch { }
}
function Invoke-Transcribe {
    param([int]$Port, [string]$WavPath, [string]$Hot, [double]$Boost)
    $url = "http://127.0.0.1:$Port/v1/audio/transcriptions"
    $client = [System.Net.Http.HttpClient]::new()
    $client.Timeout = [TimeSpan]::FromSeconds(120)
    try {
        $form = [System.Net.Http.MultipartFormDataContent]::new()
        $form.Add([System.Net.Http.StringContent]::new("en"), "language")
        $form.Add([System.Net.Http.StringContent]::new("1"), "beam_size")
        $form.Add([System.Net.Http.StringContent]::new("json"), "response_format")
        if ($Hot) {
            $form.Add([System.Net.Http.StringContent]::new($Hot), "hotwords")
            if ($Boost -gt 0) { $form.Add([System.Net.Http.StringContent]::new([string]$Boost), "hotwords_boost") }
        }
        $bytes = [System.IO.File]::ReadAllBytes($WavPath)
        $fc = [System.Net.Http.ByteArrayContent]::new($bytes)
        $fc.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse("audio/wav")
        $form.Add($fc, "file", "audio.wav")
        $resp = $client.PostAsync($url, $form).GetAwaiter().GetResult()
        $body = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if (-not $resp.IsSuccessStatusCode) { return [pscustomobject]@{ Ok = $false; Text = ""; Status = [int]$resp.StatusCode } }
        $text = ""
        try { $text = ($body | ConvertFrom-Json).text } catch { }
        return [pscustomobject]@{ Ok = $true; Text = ([string]$text).Trim(); Status = 200 }
    } finally { $client.Dispose() }
}

if (-not (Test-Path $Exe)) { throw "crispasr.exe not found at $Exe" }
$stamp  = Get-Date -Format "yyyy-MM-dd-HHmmss"
$runDir = Join-Path $OutDir "boostsweep-$stamp"
New-Item -ItemType Directory -Force -Path $runDir | Out-Null

Write-Host "Parakeet HotwordsBoost sweep $stamp  (gpu=$GpuBackend, threads=$Threads)"
Write-Host "  lexicon: $Hotwords"
Write-Host ""

$rows = New-Object System.Collections.Generic.List[object]
$port = $BasePort
foreach ($be in $Backends) {
    $port++
    $errLog = Join-Path $runDir ("{0}.stderr.log" -f $be.Name)
    $outLog = Join-Path $runDir ("{0}.stdout.log" -f $be.Name)
    Write-Host ("=== {0}  (port {1}) ===" -f $be.Name, $port)
    $proc = $null
    try {
        $proc = Start-CrispServer -Model $be.Model -Hint $be.Hint -Port $port -ErrLog $errLog -OutLog $outLog
        if (-not (Wait-Health -Port $port -TimeoutSec 150 -Proc $proc)) {
            Write-Host "  server failed to become healthy - skipping" -ForegroundColor Red
            continue
        }
        $null = Invoke-Transcribe -Port $port -WavPath (Join-Path $ClipsDir $Clips[0].File) -Hot "" -Boost 0  # warm/prime
        foreach ($cond in $Conds) {
            Write-Host ("  [{0}]" -f $cond.Name)
            foreach ($c in $Clips) {
                $res = Invoke-Transcribe -Port $port -WavPath (Join-Path $ClipsDir $c.File) -Hot $cond.Hot -Boost $cond.Boost
                $hit = Test-TermPresent -Text $res.Text -Term $c.Term
                $spur = @()
                foreach ($u in $Lexicon) { if ($u -ne $c.Term -and (Test-TermPresent -Text $res.Text -Term $u)) { $spur += $u } }
                $rows.Add([pscustomobject]@{
                    Backend = $be.Name; Cond = $cond.Name; Clip = $c.File; Term = $c.Term
                    Control = $c.Control; Hit = [bool]$hit; Spurious = ($spur -join "|"); Text = $res.Text
                })
                $mark = if ($hit) { "HIT " } elseif (-not $c.Term) { "n/a " } else { "miss" }
                Write-Host ("      {0} {1,-20} {2}" -f $mark, $c.File, $res.Text)
            }
        }
    } finally {
        Stop-CrispServer -Proc $proc
        Start-Sleep -Milliseconds 500
    }
    Write-Host ""
}

$rows | Export-Csv -Path (Join-Path $runDir "rows.csv") -NoTypeInformation -Encoding UTF8
$rows | ConvertTo-Json -Depth 5 | Out-File -FilePath (Join-Path $runDir "rows.json") -Encoding UTF8

# ---- score: per backend x cond, target-hit / control-integrity / garble / spurious ----
function HitFrac { param($subset) $d = @($subset).Count; if ($d -eq 0) { return "n/a" } "{0}/{1}" -f @($subset | Where-Object Hit).Count, $d }

Write-Host "==================== SUMMARY ===================="
foreach ($be in ($Backends | Select-Object -ExpandProperty Name)) {
    $baseByClip = @{}
    foreach ($r in ($rows | Where-Object { $_.Backend -eq $be -and $_.Cond -eq "off" })) { $baseByClip[$r.Clip] = (Normalize-Text $r.Text) }
    $sum = New-Object System.Collections.Generic.List[object]
    foreach ($cond in ($Conds | Select-Object -ExpandProperty Name)) {
        $set = @($rows | Where-Object { $_.Backend -eq $be -and $_.Cond -eq $cond })
        if ($set.Count -eq 0) { continue }
        $tgt = @($set | Where-Object { -not $_.Control })
        $ctl = @($set | Where-Object { $_.Control -and $_.Term })
        # garble = # of control+neutral clips whose text changed vs the OFF baseline
        $garble = 0
        foreach ($r in ($set | Where-Object { $_.Control })) {
            if ($baseByClip.ContainsKey($r.Clip) -and (Normalize-Text $r.Text) -ne $baseByClip[$r.Clip]) { $garble++ }
        }
        $spur = @($set | Where-Object { $_.Spurious }).Count
        $sum.Add([pscustomobject]@{
            Cond = $cond; TargetHit = (HitFrac $tgt); ControlHit = (HitFrac $ctl)
            CtlChangedVsOff = ("{0}/4" -f $garble); SpuriousInj = $spur
        })
    }
    Write-Host ""
    Write-Host ("--- {0} ---" -f $be)
    $sum | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
}
Write-Host ("Artifacts: {0}" -f $runDir)
Write-Host "Done."
