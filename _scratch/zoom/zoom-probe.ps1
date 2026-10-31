<#
  Live probe of Zoom's Scribe API (AI Services, Fast mode: POST /v2/aiservices/scribe/transcribe)
  on the owner's six scripted clips, the 32 s joined take, room tone and longer 16 kHz takes.
  It answers what the docs don't: how the audio can be sent (the docs offer a URL or a data
  URL, with a 2 MB body cap for the latter), which auth works, what comes back, and which
  options help an English dictation.

  PAID: every request bills audio (20 cents an hour standard, 35 Pro). The key is read from
  %APPDATA%\.WhisperInk\zoom-api-key.txt, else from the zoom-scribe provider in config.json,
  and never printed. Transcripts of these scripted clips go to the console and to a CSV under
  %APPDATA% (not the repo, which is in OneDrive).

  The clips are converted to 16 kHz mono 16-bit with ffmpeg first, because that's what
  WhisperInk records and sends (and the body cap is about size).

  USAGE  pwsh .\zoom-probe.ps1                 the wire checks, then every condition
         pwsh .\zoom-probe.ps1 -Only wire      the wire checks alone
         pwsh .\zoom-probe.ps1 -Only list,pro  only those conditions
         pwsh .\zoom-probe.ps1 -Reps 2         two requests per clip and condition

  Conditions, each on every clip, sent as a multipart file upload with the config as a JSON
  `config` field (undocumented, but accepted, and what WhisperInk sends; model zoom-scribe-en
  unless named):
    nolist   language=en-US, no list
    list     language=en-US, the shared list as config.vocabulary.phrases
    segnone  list + segmentation_mode=none (Zoom's own voice-activity detection off)
    pro      list, model zoom-scribe-en-pro
#>
param([string[]]$Only, [int]$Reps = 1, [string]$Bias = (Join-Path $PSScriptRoot '..\biasing'))
$ErrorActionPreference = 'Stop'
$root = Join-Path $env:APPDATA '.WhisperInk'
$cfg  = Get-Content (Join-Path $root 'config.json') -Raw | ConvertFrom-Json
$keyFile = Join-Path $root 'zoom-api-key.txt'
$key = if (Test-Path $keyFile) { (Get-Content $keyFile -Raw).Trim() } else { ($cfg.Providers | Where-Object Id -eq 'zoom-scribe').ApiKey }
if ([string]::IsNullOrWhiteSpace($key)) { throw "No Zoom key: save it to $keyFile or the zoom-scribe provider." }
$shared = @($cfg.ContextBiasTerms | Where-Object { $_ })
$url  = 'https://api.zoom.us/v2/aiservices/scribe/transcribe'
$outDir = Join-Path $root 'ab-corpus\results'
New-Item -ItemType Directory -Force $outDir | Out-Null
$csv = Join-Path $outDir "zoom-probe-$(Get-Date -Format yyyy-MM-dd-HHmm).csv"

# 16 kHz mono copies of the clips, in a temp folder.
$tmp = Join-Path ([IO.Path]::GetTempPath()) 'whisperink-zoom-probe'
New-Item -ItemType Directory -Force $tmp | Out-Null
function To16k([string]$path) {
  $out = Join-Path $tmp ([IO.Path]::GetFileName($path))
  if (-not (Test-Path $out)) { & ffmpeg -hide_banner -loglevel error -y -i $path -ac 1 -ar 16000 -c:a pcm_s16le $out; if ($LASTEXITCODE) { throw "ffmpeg failed on $path" } }
  Get-Item $out
}
$clips = @(Get-ChildItem (Join-Path $Bias 'clips\*.wav') | Sort-Object Name | ForEach-Object { To16k $_.FullName }) +
         @((To16k (Join-Path $Bias 'joined\all_six_joined.wav')), (Get-Item (Join-Path $Bias 'tails\room_tone_only.wav')))
if ($clips.Count -lt 8) { throw "Clips not found under $Bias (they're git-ignored; pass -Bias <main checkout>\_scratch\biasing)." }

Add-Type -AssemblyName System.Net.Http
$http = [System.Net.Http.HttpClient]::new()
$http.Timeout = [TimeSpan]::FromMinutes(5)

function WavSeconds([byte[]]$w) {
  $pos = 12; $rate = 16000; $ch = 1; $bits = 16
  while ($pos + 8 -le $w.Length) {
    $id = [Text.Encoding]::ASCII.GetString($w, $pos, 4); $size = [BitConverter]::ToUInt32($w, $pos + 4)
    if ($id -eq 'fmt ') { $ch = [BitConverter]::ToInt16($w, $pos + 10); $rate = [BitConverter]::ToInt32($w, $pos + 12); $bits = [BitConverter]::ToInt16($w, $pos + 22) }
    elseif ($id -eq 'data') { return [Math]::Min([double]$size, $w.Length - $pos - 8) / ($rate * $ch * $bits / 8) }
    $pos += 8 + $size + ($size % 2)
  }
  0
}

# One POST of the JSON body {file, config}. -Auth picks the header; -Mime the data URL's type;
# -Bare sends plain base64 with no data: prefix. -Multipart and -Raw try the uploads the docs
# don't mention: a multipart file part with the config as a JSON field, and the WAV as the body.
function Send([byte[]]$wav, $config, [string]$Auth = 'x-api-key', [string]$Mime = 'audio/wav', [switch]$Bare, [switch]$Multipart, [switch]$Raw) {
  $req = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, $url)
  if ($Auth -eq 'bearer') { $req.Headers.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $key) }
  else { $null = $req.Headers.TryAddWithoutValidation('x-api-key', $key) }
  $cfgJson = $config | ConvertTo-Json -Depth 6 -Compress
  if ($Multipart) {
    $mp = [System.Net.Http.MultipartFormDataContent]::new()
    $mp.Add([System.Net.Http.StringContent]::new($cfgJson), 'config')
    $fc = [System.Net.Http.ByteArrayContent]::new($wav)
    $fc.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse('audio/wav')
    $mp.Add($fc, 'file', 'audio.wav')
    $req.Content = $mp
  } elseif ($Raw) {
    $req.Content = [System.Net.Http.ByteArrayContent]::new($wav)
    $req.Content.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse('audio/wav')
  } else {
    $b64 = [Convert]::ToBase64String($wav)
    $file = if ($Bare) { $b64 } else { "data:$Mime;base64,$b64" }
    $body = '{"file":"' + $file + '","config":' + $cfgJson + '}'
    $req.Content = [System.Net.Http.StringContent]::new($body, [Text.Encoding]::UTF8, 'application/json')
  }
  $len = $req.Content.Headers.ContentLength
  $sw = [Diagnostics.Stopwatch]::StartNew()
  $resp = $http.SendAsync($req).GetAwaiter().GetResult()
  $text = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
  $sw.Stop()
  $j = $null; try { $j = $text | ConvertFrom-Json } catch { }
  [pscustomobject]@{ Status = [int]$resp.StatusCode; Ms = $sw.ElapsedMilliseconds; Body = $text; Json = $j; Bytes = $len }
}

function Cfg([string]$model = 'zoom-scribe-en', [string[]]$terms, [string]$seg, [string]$lang = 'en-US') {
  $c = [ordered]@{ language = $lang }
  if ($model) { $c.model = $model }
  $c.word_time_offsets = $true
  if ($seg) { $c.segmentation_mode = $seg }
  if ($terms) { $c.vocabulary = [ordered]@{ phrases = @($terms) } }
  $c
}

function Line($name, $r) {
  $err = if ($r.Status -ge 400) { '  ' + $r.Body.Substring(0, [Math]::Min(300, $r.Body.Length)) } else { '' }
  "{0,-34} HTTP {1} {2,6} ms  body {3,9:N0} B{4}" -f $name, $r.Status, $r.Ms, $r.Bytes, $err
  if ($r.Status -eq 200) {
    $res = $r.Json.result
    $words = @($res.segments | ForEach-Object { $_.words }).Count
    $lastEnd = (@($res.segments | ForEach-Object { $_.words } | ForEach-Object { $_.end }) | Measure-Object -Maximum).Maximum
    "{0,-34} model={1} duration_sec={2} segments={3} words={4} lastWordEnd={5} keys={6}" -f '', $r.Json.model, $r.Json.duration_sec, @($res.segments).Count, $words, $lastEnd, (($r.Json.PSObject.Properties.Name) -join ',')
    "{0,-34} text=[{1}]" -f '', $res.text_display
  }
}

$neutral = [IO.File]::ReadAllBytes(($clips | Where-Object BaseName -eq 'neutral').FullName)
# Warm the connection, as PrewarmConnection does, so the first timing isn't a cold TLS handshake.
try { $null = $http.SendAsync([System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Head, 'https://api.zoom.us/')).GetAwaiter().GetResult() } catch { }

"== wire checks (neutral clip, $([Math]::Round((WavSeconds $neutral), 1)) s at 16 kHz mono; shared list $($shared.Count) terms)"
$wire = [ordered]@{
  'baseline (x-api-key, en-US)'   = { Send $neutral (Cfg) }
  'again (warm)'                  = { Send $neutral (Cfg) }
  'Bearer auth'                   = { Send $neutral (Cfg) -Auth bearer }
  'language en'                   = { Send $neutral (Cfg -lang 'en') }
  'no model (automatic)'          = { Send $neutral (Cfg -model '') }
  'model zoom-scribe-en-pro'      = { Send $neutral (Cfg 'zoom-scribe-en-pro') }
  'vocabulary (shared list)'      = { Send $neutral (Cfg -terms $shared) }
  'segmentation_mode none'        = { Send $neutral (Cfg -seg 'none') }
  'data URL audio/x-wav'          = { Send $neutral (Cfg) -Mime 'audio/x-wav' }
  'bare base64, no data: prefix'  = { Send $neutral (Cfg) -Bare }
  'multipart file upload'         = { Send $neutral (Cfg) -Multipart }
  'raw WAV body'                  = { Send $neutral (Cfg) -Raw }
}
foreach ($name in $wire.Keys) { Line $name (& $wire[$name]) }

"`n== size (16 kHz mono WAV as a data URL; the docs cap the body at 2 MB)"
foreach ($f in 'five+tail12s', 'straddle60+tail2s', 'long_3x_six') {
  $wav = [IO.File]::ReadAllBytes((Join-Path $Bias "tails\$f.wav"))
  Line ("{0} ({1:N1} s)" -f $f, (WavSeconds $wav)) (Send $wav (Cfg -terms $shared))
}

# segmentation_mode=none refused the 111 s take ("exceeds max audio segment duration after
# disabling VAD", 2026-10-30): find where it stops working, as a file upload.
"`n== segmentation_mode none by length (multipart)"
$joined = [IO.File]::ReadAllBytes(($clips | Where-Object BaseName -eq 'all_six_joined').FullName)
Line ("all_six_joined ({0:N1} s)" -f (WavSeconds $joined)) (Send $joined (Cfg -terms $shared -seg 'none') -Multipart)
foreach ($f in 'five+tail12s', 'straddle60+tail2s') {
  $wav = [IO.File]::ReadAllBytes((Join-Path $Bias "tails\$f.wav"))
  Line ("{0} ({1:N1} s)" -f $f, (WavSeconds $wav)) (Send $wav (Cfg -terms $shared -seg 'none') -Multipart)
}
if ($Only -contains 'wire') { return }

$conds = [ordered]@{
  nolist  = @{ M = 'zoom-scribe-en';     T = $null;   S = $null }
  list    = @{ M = 'zoom-scribe-en';     T = $shared; S = $null }
  segnone = @{ M = 'zoom-scribe-en';     T = $shared; S = 'none' }
  pro     = @{ M = 'zoom-scribe-en-pro'; T = $shared; S = $null }
}
if ($Only) { $keep = [ordered]@{}; foreach ($k in $conds.Keys) { if ($Only -contains $k) { $keep[$k] = $conds[$k] } }; $conds = $keep }

# Each clip's own term, as the clips tables in CLAUDE.md 4.3 judge it.
$expect = @{ hematochezia_1 = 'hematochezia'; hematochezia_2 = 'hematochezia'; ureterolithiasis = 'ureterolithiasis'
             biliary_colic = 'biliary colic'; ureteral_colic = 'ureteral colic' }
$rows = [System.Collections.Generic.List[object]]::new()
function Judge($clip, $text) {
  if ($expect.ContainsKey($clip)) { return $(if ($text -match "(?i)\b$([regex]::Escape($expect[$clip]))\b") { 'OK' } else { 'MISS' }) }
  if ($clip -eq 'all_six_joined') {
    $h = ([regex]::Matches($text, '(?i)\bhematochezia\b')).Count
    $u = ([regex]::Matches($text, '(?i)\bureterolithiasis\b')).Count
    $b = ([regex]::Matches($text, '(?i)\bbiliary colic\b')).Count
    $c = ([regex]::Matches($text, '(?i)\bureteral colic\b')).Count
    return "h$h/2 u$u/1 b$b/1 c$c/1"
  }
  $listed = @($shared | Where-Object { $text -match "(?i)(?<!\w)$([regex]::Escape($_))(?!\w)" }).Count
  if ($clip -eq 'room_tone_only') { return $(if ($text.Trim().Length -eq 0) { 'empty' } else { "TEXT ($listed list terms)" }) }
  "list terms: $listed"
}

"`n== conditions x clips ($Reps rep(s))"
foreach ($clip in $clips) {
  $wav = [IO.File]::ReadAllBytes($clip.FullName)
  "`n-- $($clip.BaseName) ($([Math]::Round((WavSeconds $wav), 1)) s)"
  foreach ($name in $conds.Keys) {
    $c = $conds[$name]
    foreach ($rep in 1..$Reps) {
      $r = Send $wav (Cfg $c.M $c.T $c.S) -Multipart
      $text = if ($r.Status -eq 200) { [string]$r.Json.result.text_display } else { "<HTTP $($r.Status): $($r.Body.Substring(0, [Math]::Min(200, $r.Body.Length)))>" }
      $verdict = if ($r.Status -eq 200) { Judge $clip.BaseName $text } else { 'ERROR' }
      "   {0,-8} r{1} {2,6} ms  {3,-16} [{4}]" -f $name, $rep, $r.Ms, $verdict, $text
      $rows.Add([pscustomobject]@{ Cond = $name; Clip = $clip.BaseName; Rep = $rep; Status = $r.Status; Ms = $r.Ms; Verdict = $verdict
                                   AudioS = [Math]::Round((WavSeconds $wav), 2); DurationS = $r.Json.duration_sec; Text = $text })
    }
  }
}

$rows | Export-Csv $csv -NoTypeInformation -Encoding utf8
"`n== summary (clip terms right, median ms)"
foreach ($name in $conds.Keys) {
  $r = @($rows | Where-Object { $_.Cond -eq $name -and $expect.ContainsKey($_.Clip) })
  $ok = @($r | Where-Object Verdict -eq 'OK').Count
  $ms = @($r | Where-Object Status -eq 200 | ForEach-Object Ms | Sort-Object)
  $med = if ($ms.Count) { $ms[[int][Math]::Floor($ms.Count / 2)] } else { 0 }
  "{0,-8} {1}/{2} clips right   median {3} ms" -f $name, $ok, $r.Count, $med
}
"wrote $csv"
