#requires -Version 5.1
<#
  _cohere_baseline.ps1 - Cohere Q6_K base accuracy on the clinical clips.
  Decides whether the user's accuracy-favorite local model even misses the hard
  terms (hematochezia / ureterolithiasis). Also re-confirms hotwords are a no-op
  on the cohere backend (off vs on == identical). Greedy (beam 1) = deployed default.
#>
[CmdletBinding()]
param(
    [string]$GpuBackend = "auto",
    [int]$Port          = 8213,
    [string]$ClipsDir   = (Join-Path $PSScriptRoot "clips")
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Net.Http | Out-Null

$ExeDir  = Join-Path $env:APPDATA ".WhisperInk\cohere-gguf"
$Exe     = Join-Path $ExeDir "crispasr.exe"
$Model   = "cohere-transcribe-q6_k.gguf"
$Threads = [Math]::Min(8, [Environment]::ProcessorCount)
$Hotwords = "hematochezia,ureterolithiasis,biliary colic,ureteral colic"

$Clips = @(
    [pscustomobject]@{ File = "hematochezia_1.wav";   Term = "hematochezia"     }
    [pscustomobject]@{ File = "hematochezia_2.wav";   Term = "hematochezia"     }
    [pscustomobject]@{ File = "ureterolithiasis.wav"; Term = "ureterolithiasis" }
    [pscustomobject]@{ File = "biliary_colic.wav";    Term = "biliary colic"    }
    [pscustomobject]@{ File = "ureteral_colic.wav";   Term = "ureteral colic"   }
    [pscustomobject]@{ File = "neutral.wav";          Term = ""                 }
)

function Normalize-Text { param([string]$s) if (-not $s) { return "" } " " + (($s.ToLowerInvariant() -replace "[^a-z0-9]+"," ").Trim()) + " " }
function Test-TermPresent { param([string]$Text,[string]$Term) if (-not $Term) { return $false } (Normalize-Text $Text).Contains((Normalize-Text $Term)) }
function Wait-Health { param([int]$Port,[int]$TimeoutSec=150,[System.Diagnostics.Process]$Proc)
    $deadline=(Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) { if ($Proc -and $Proc.HasExited){return $false}
        try { if ((Invoke-WebRequest "http://127.0.0.1:$Port/health" -UseBasicParsing -TimeoutSec 2).StatusCode -eq 200){return $true} } catch {}
        Start-Sleep -Milliseconds 300 } ; return $false }
function Invoke-Transcribe { param([int]$Port,[string]$WavPath,[string]$Hot)
    $client=[System.Net.Http.HttpClient]::new(); $client.Timeout=[TimeSpan]::FromSeconds(120)
    try {
        $form=[System.Net.Http.MultipartFormDataContent]::new()
        $form.Add([System.Net.Http.StringContent]::new("en"),"language")
        $form.Add([System.Net.Http.StringContent]::new("1"),"beam_size")
        $form.Add([System.Net.Http.StringContent]::new("json"),"response_format")
        if ($Hot) { $form.Add([System.Net.Http.StringContent]::new($Hot),"hotwords") }
        $bytes=[System.IO.File]::ReadAllBytes($WavPath)
        $fc=[System.Net.Http.ByteArrayContent]::new($bytes)
        $fc.Headers.ContentType=[System.Net.Http.Headers.MediaTypeHeaderValue]::Parse("audio/wav")
        $form.Add($fc,"file","audio.wav")
        $resp=$client.PostAsync("http://127.0.0.1:$Port/v1/audio/transcriptions",$form).GetAwaiter().GetResult()
        $body=$resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if (-not $resp.IsSuccessStatusCode){return [pscustomobject]@{Text="";Status=[int]$resp.StatusCode}}
        $t=""; try { $t=($body|ConvertFrom-Json).text } catch {}
        return [pscustomobject]@{Text=([string]$t).Trim();Status=200}
    } finally { $client.Dispose() }
}

if (-not (Test-Path $Exe)) { throw "crispasr.exe not found" }
$srvArgs=@("--server","--host","127.0.0.1","--port","$Port","-m",(Join-Path $ExeDir $Model),"-t","$Threads","-bs","1","--backend","cohere")
if ($GpuBackend -eq "cpu"){$srvArgs+="-ng"} elseif ($GpuBackend -and $GpuBackend -ne "auto"){$srvArgs+=@("--gpu-backend",$GpuBackend)}
Write-Host "Cohere Q6_K baseline (gpu=$GpuBackend, beam=1, port=$Port)"
$proc=Start-Process -FilePath $Exe -ArgumentList $srvArgs -WorkingDirectory $ExeDir -NoNewWindow -PassThru `
        -RedirectStandardError (Join-Path $env:TEMP "cohere_base.stderr.log") -RedirectStandardOutput (Join-Path $env:TEMP "cohere_base.stdout.log")
try {
    if (-not (Wait-Health -Port $Port -Proc $proc)) { Write-Host "server failed health" -ForegroundColor Red; return }
    $null = Invoke-Transcribe -Port $Port -WavPath (Join-Path $ClipsDir $Clips[0].File) -Hot ""   # warm
    foreach ($cond in @(@{N="baseline (no hotwords)";H=""}, @{N="with hotwords (no-op check)";H=$Hotwords})) {
        Write-Host ""; Write-Host ("[{0}]" -f $cond.N)
        foreach ($c in $Clips) {
            $res = Invoke-Transcribe -Port $Port -WavPath (Join-Path $ClipsDir $c.File) -Hot $cond.H
            $hit = Test-TermPresent -Text $res.Text -Term $c.Term
            $mark = if ($hit){"HIT "} elseif (-not $c.Term){"n/a "} else {"miss"}
            Write-Host ("   {0} {1,-20} {2}" -f $mark,$c.File,$res.Text)
        }
    }
} finally {
    try { if (-not $proc.HasExited){ & taskkill /PID $proc.Id /T /F 2>$null | Out-Null } } catch {}
}
Write-Host ""; Write-Host "Done."
