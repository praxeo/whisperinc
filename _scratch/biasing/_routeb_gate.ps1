#requires -Version 5.1
<#
  _routeb_gate.ps1 - Cohere Route B (decoder context-prompt) gate.
  Runs the THROWAWAY test binary (build-sync\bin\Release\crispasr.exe, built with the
  CRISPASR_COHERE_CTX splice) on the clinical clips, with the context slot:
    (A) empty   -> must reproduce the cohere baseline (proves the edit is a no-op when unset)
    (B) primed  -> "ureterolithiasis hematochezia" spliced after <|startofcontext|>
  If B corrects/changes the missed terms vs A, the checkpoint HONORS context conditioning
  (Route B alive). If A == B byte-for-byte, the slot is ignored (Route B dead).
#>
[CmdletBinding()]
param(
    [int]$Port        = 8214,
    [string]$ClipsDir = (Join-Path $PSScriptRoot "clips"),
    [string]$CtxPrime = "ureterolithiasis hematochezia"
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Net.Http | Out-Null

$TestExe = "C:\Users\obert\OneDrive\Desktop\CrispASR\build-sync\bin\Release\crispasr.exe"
$Model   = Join-Path $env:APPDATA ".WhisperInk\cohere-gguf\cohere-transcribe-q6_k.gguf"
$Threads = [Math]::Min(8, [Environment]::ProcessorCount)

$Clips = @("hematochezia_1.wav","hematochezia_2.wav","ureterolithiasis.wav","biliary_colic.wav","ureteral_colic.wav","neutral.wav")

function Wait-Health { param([int]$Port,[System.Diagnostics.Process]$Proc,[int]$TimeoutSec=150)
    $deadline=(Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) { if ($Proc -and $Proc.HasExited){return $false}
        try { if ((Invoke-WebRequest "http://127.0.0.1:$Port/health" -UseBasicParsing -TimeoutSec 2).StatusCode -eq 200){return $true} } catch {}
        Start-Sleep -Milliseconds 300 }; return $false }
function Invoke-Transcribe { param([int]$Port,[string]$WavPath)
    $client=[System.Net.Http.HttpClient]::new(); $client.Timeout=[TimeSpan]::FromSeconds(120)
    try {
        $form=[System.Net.Http.MultipartFormDataContent]::new()
        $form.Add([System.Net.Http.StringContent]::new("en"),"language")
        $form.Add([System.Net.Http.StringContent]::new("1"),"beam_size")
        $form.Add([System.Net.Http.StringContent]::new("json"),"response_format")
        $bytes=[System.IO.File]::ReadAllBytes($WavPath)
        $fc=[System.Net.Http.ByteArrayContent]::new($bytes)
        $fc.Headers.ContentType=[System.Net.Http.Headers.MediaTypeHeaderValue]::Parse("audio/wav")
        $form.Add($fc,"file","audio.wav")
        $resp=$client.PostAsync("http://127.0.0.1:$Port/v1/audio/transcriptions",$form).GetAwaiter().GetResult()
        $body=$resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        $t=""; try { $t=($body|ConvertFrom-Json).text } catch {}
        return ([string]$t).Trim()
    } finally { $client.Dispose() }
}
function Run-Condition { param([string]$Name,[string]$CtxValue)
    if ($CtxValue) { $env:CRISPASR_COHERE_CTX = $CtxValue } else { Remove-Item Env:CRISPASR_COHERE_CTX -ErrorAction SilentlyContinue }
    $env:CRISPASR_VERBOSE = "1"
    $err = Join-Path $env:TEMP ("routeb_{0}.stderr.log" -f $Name)
    $srvArgs=@("--server","--host","127.0.0.1","--port","$Port","-m",$Model,"-t","$Threads","-bs","1","--backend","cohere")
    $proc=Start-Process -FilePath $TestExe -ArgumentList $srvArgs -WorkingDirectory (Split-Path $TestExe) -NoNewWindow -PassThru `
            -RedirectStandardError $err -RedirectStandardOutput (Join-Path $env:TEMP ("routeb_{0}.stdout.log" -f $Name))
    $out = [ordered]@{}
    try {
        if (-not (Wait-Health -Port $Port -Proc $proc)) { Write-Host "  [$Name] server failed health" -ForegroundColor Red; return $out }
        $null = Invoke-Transcribe -Port $Port -WavPath (Join-Path $ClipsDir $Clips[0])  # warm
        foreach ($c in $Clips) { $out[$c] = Invoke-Transcribe -Port $Port -WavPath (Join-Path $ClipsDir $c) }
    } finally {
        try { if (-not $proc.HasExited){ & taskkill /PID $proc.Id /T /F 2>$null | Out-Null } } catch {}
        Start-Sleep -Milliseconds 500
    }
    # surface the [RouteB] injection log line if present
    if (Test-Path $err) { Select-String -Path $err -Pattern "RouteB" -SimpleMatch | Select-Object -First 1 | ForEach-Object { Write-Host ("  inject-log: " + $_.Line.Trim()) -ForegroundColor DarkGray } }
    return $out
}

if (-not (Test-Path $TestExe)) { throw "test exe not found: $TestExe" }
if (-not (Test-Path $Model))   { throw "model not found: $Model" }
Write-Host "Route B gate  (test binary: $TestExe)"
Write-Host ("ctxPrime = '$CtxPrime'")
Write-Host ""

Write-Host "[A] context EMPTY (baseline sanity)"
$A = Run-Condition -Name "off" -CtxValue ""
Write-Host ""
Write-Host "[B] context PRIMED"
$B = Run-Condition -Name "prime" -CtxValue $CtxPrime
Write-Host ""

Write-Host "==================== COMPARISON ===================="
$anyChange = $false
foreach ($c in $Clips) {
    $changed = ($A[$c] -ne $B[$c])
    if ($changed) { $anyChange = $true }
    Write-Host ("--- {0}  {1}" -f $c, $(if($changed){"[CHANGED]"}else{"[same]"})) -ForegroundColor $(if($changed){"Yellow"}else{"Gray"})
    Write-Host ("   A(empty) : {0}" -f $A[$c])
    Write-Host ("   B(primed): {0}" -f $B[$c])
}
Write-Host ""
if ($anyChange) {
    Write-Host "VERDICT: context slot is LIVE - priming changed output. Route B is viable; refine the glossary." -ForegroundColor Green
} else {
    Write-Host "VERDICT: byte-identical A vs B - the checkpoint IGNORES the context slot. Route B is dead." -ForegroundColor Red
}
