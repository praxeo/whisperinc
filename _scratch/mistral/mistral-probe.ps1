<#
  Live probe of Mistral's transcription API (Voxtral Mini Transcribe 2) on the owner's
  six scripted clips, the 32 s joined take and room tone, plus a 111 s take for the
  shipped request. It answers what the docs don't: is the request WhisperInk sends
  accepted, what comes back, and which options help an English dictation.

  PAID: every request bills audio ($0.003 a minute; the full run is about 10 minutes
  of audio). The key comes from config.json (the `mistral` provider) and is never
  printed. Transcripts of these scripted clips go to the console and to a CSV under
  %APPDATA% (not the repo, which is in OneDrive). Mistral keeps API inputs for 30 days
  unless zero retention was granted, so never point this at real dictations.

  USAGE  pwsh .\mistral-probe.ps1                 the wire checks, then every condition
         pwsh .\mistral-probe.ps1 -Only wire      the wire checks alone
         pwsh .\mistral-probe.ps1 -Reps 2         two requests per clip and condition

  Conditions, each on every clip (the model is voxtral-mini-latest unless named):
    nolist    language=en, no list
    list      language=en, the shared list as one context_bias field per term (shipped)
    comma     the same list as ONE comma-joined field (what WhisperInk sent before 2026-09-29)
    t0        list + temperature=0
    pin2602   list, model voxtral-mini-2602
    ts        list + timestamp_granularities=segment, no language (the API refuses both)
#>
param([string[]]$Only, [int]$Reps = 1)
$ErrorActionPreference = 'Stop'
$root = Join-Path $env:APPDATA '.WhisperInk'
$cfg  = Get-Content (Join-Path $root 'config.json') -Raw | ConvertFrom-Json
$key  = ($cfg.Providers | Where-Object Id -eq 'mistral').ApiKey
if ([string]::IsNullOrWhiteSpace($key)) { throw 'No Mistral key in config.json: add it in Configure Providers first.' }
$raw    = @($cfg.ContextBiasTerms | Where-Object { $_ })
# Mistral refuses the whole request (400, code 3051) if any term holds a space or a comma; its docs
# write a phrase with underscores ("American_people"). HttpTranscriber.MistralContextBias does the same.
$shared = @($raw | ForEach-Object { (($_ -replace ',', ' ').Trim()) -replace '\s+', '_' } | Where-Object { $_ })
$url  = 'https://api.mistral.ai/v1/audio/transcriptions'
$bias = Join-Path $PSScriptRoot '..\biasing'
$clips = @(Get-ChildItem (Join-Path $bias 'clips\*.wav') | Sort-Object Name) +
         @(Get-Item (Join-Path $bias 'joined\all_six_joined.wav'), (Join-Path $bias 'tails\room_tone_only.wav'))
$long  = Get-Item (Join-Path $bias 'tails\long_3x_six.wav')
$outDir = Join-Path $root 'ab-corpus\results'
New-Item -ItemType Directory -Force $outDir | Out-Null
$csv = Join-Path $outDir "mistral-probe-$(Get-Date -Format yyyy-MM-dd-HHmm).csv"

Add-Type -AssemblyName System.Net.Http
$http = [System.Net.Http.HttpClient]::new()
$http.Timeout = [TimeSpan]::FromMinutes(5)
$http.DefaultRequestHeaders.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $key)

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

# One POST. $fields are sent in order before the list; the file goes last, as WhisperInk sends it.
# -Chunked sends the body with no Content-Length (what a streamed upload would do);
# -OpenEnded rewrites the WAV's RIFF and data sizes to 0xFFFFFFFF (a header written before the length is known).
function Send([byte[]]$wav, $fields, [string[]]$terms, [switch]$Comma, [switch]$Chunked, [switch]$OpenEnded) {
  $mp = [System.Net.Http.MultipartFormDataContent]::new()
  foreach ($k in $fields.Keys) { $mp.Add([System.Net.Http.StringContent]::new([string]$fields[$k]), $k) }
  if ($terms) {
    if ($Comma) { $mp.Add([System.Net.Http.StringContent]::new(($terms -join ',')), 'context_bias') }
    else { foreach ($t in $terms) { $mp.Add([System.Net.Http.StringContent]::new($t), 'context_bias') } }
  }
  $bytes = $wav
  if ($OpenEnded) {
    $bytes = [byte[]]$wav.Clone(); $ff = [byte[]](255, 255, 255, 255)
    [Array]::Copy($ff, 0, $bytes, 4, 4)
    $pos = 12
    while ($pos + 8 -le $bytes.Length) {
      $id = [Text.Encoding]::ASCII.GetString($bytes, $pos, 4); $size = [BitConverter]::ToUInt32($wav, $pos + 4)
      if ($id -eq 'data') { [Array]::Copy($ff, 0, $bytes, $pos + 4, 4); break }
      $pos += 8 + $size + ($size % 2)
    }
  }
  $fc = [System.Net.Http.ByteArrayContent]::new($bytes)
  $fc.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse('audio/wav')
  $mp.Add($fc, 'file', 'audio.wav')
  $req = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, $url)
  $req.Content = $mp
  if ($Chunked) { $req.Headers.TransferEncodingChunked = $true }
  $sw = [Diagnostics.Stopwatch]::StartNew()
  $resp = $http.SendAsync($req).GetAwaiter().GetResult()
  $body = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
  $sw.Stop()
  $j = $null; try { $j = $body | ConvertFrom-Json } catch { }
  [pscustomobject]@{ Status = [int]$resp.StatusCode; Ms = $sw.ElapsedMilliseconds; Body = $body; Json = $j }
}

$base = [ordered]@{ model = 'voxtral-mini-latest'; language = 'en' }
$neutral = [IO.File]::ReadAllBytes(($clips | Where-Object BaseName -eq 'neutral').FullName)

# Warm the connection the way PrewarmConnection does, so the first timing isn't a cold TLS handshake.
$null = $http.SendAsync([System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Head, 'https://api.mistral.ai/')).GetAwaiter().GetResult()

"== wire checks (neutral clip, $([Math]::Round((WavSeconds $neutral), 1)) s; shared list $($shared.Count) terms, multi-word: $(($shared | Where-Object { $_ -match '\s' }) -join ' | '))"
$wire = [ordered]@{
  'raw list, spaces kept'       = { Send $neutral $base $raw }
  'raw list, one comma field'   = { Send $neutral $base $raw -Comma }
  'list (shipped)'              = { Send $neutral $base $shared }
  'list, chunked body'          = { Send $neutral $base $shared -Chunked }
  'list, chunked, open-ended WAV' = { Send $neutral $base $shared -Chunked -OpenEnded }
  'language + timestamps'       = { Send $neutral ([ordered]@{ model = 'voxtral-mini-latest'; language = 'en'; timestamp_granularities = 'segment' }) $shared }
  'model voxtral-mini-2602'     = { Send $neutral ([ordered]@{ model = 'voxtral-mini-2602'; language = 'en' }) $shared }
  'temperature 0'               = { Send $neutral ([ordered]@{ model = 'voxtral-mini-latest'; language = 'en'; temperature = '0' }) $shared }
}
foreach ($name in $wire.Keys) {
  $r = & $wire[$name]
  $keys = if ($r.Json) { ($r.Json.PSObject.Properties.Name) -join ',' } else { '' }
  $usage = if ($r.Json.usage) { ($r.Json.usage | ConvertTo-Json -Compress) } else { '' }
  $err = if ($r.Status -ge 400) { '  ' + $r.Body.Substring(0, [Math]::Min(300, $r.Body.Length)) } else { '' }
  "{0,-32} HTTP {1} {2,5} ms  keys: {3}  usage: {4}{5}" -f $name, $r.Status, $r.Ms, $keys, $usage, $err
  if ($r.Status -eq 200) {
    "{0,-32} model={1} language={2} segments={3} text=[{4}]" -f '', $r.Json.model, $r.Json.language, @($r.Json.segments).Count, $r.Json.text
  }
}
if ($Only -contains 'wire') { return }

$conds = [ordered]@{
  nolist  = @{ F = $base; T = $null }
  list    = @{ F = $base; T = $shared }
  comma   = @{ F = $base; T = $shared; Comma = $true }
  t0      = @{ F = [ordered]@{ model = 'voxtral-mini-latest'; language = 'en'; temperature = '0' }; T = $shared }
  pin2602 = @{ F = [ordered]@{ model = 'voxtral-mini-2602'; language = 'en' }; T = $shared }
  ts      = @{ F = [ordered]@{ model = 'voxtral-mini-latest'; timestamp_granularities = 'segment' }; T = $shared }
}
if ($Only) { $keep = [ordered]@{}; foreach ($k in $conds.Keys) { if ($Only -contains $k) { $keep[$k] = $conds[$k] } }; $conds = $keep }

# Each clip's own term, as the clips tables in CLAUDE.md 4.3 judge it.
$expect = @{ hematochezia_1 = 'hematochezia'; hematochezia_2 = 'hematochezia'; ureterolithiasis = 'ureterolithiasis'
             biliary_colic = 'biliary colic'; ureteral_colic = 'ureteral colic' }
$rows = [System.Collections.Generic.List[object]]::new()
function Judge($clip, $text) {
  if ($expect.ContainsKey($clip)) { return $(if ($text -match "(?i)\b$([regex]::Escape($expect[$clip]))\b") { 'OK' } else { 'MISS' }) }
  if ($clip -eq 'all_six_joined') {
    $h =([regex]::Matches($text, '(?i)\bhematochezia\b')).Count
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
      $r = Send $wav $c.F $c.T -Comma:([bool]$c.Comma)
      $text = if ($r.Status -eq 200) { [string]$r.Json.text } else { "<HTTP $($r.Status): $($r.Body.Substring(0, [Math]::Min(200, $r.Body.Length)))>" }
      $verdict = if ($r.Status -eq 200) { Judge $clip.BaseName $text } else { 'ERROR' }
      "   {0,-8} r{1} {2,5} ms  {3,-16} [{4}]" -f $name, $rep, $r.Ms, $verdict, $text
      $rows.Add([pscustomobject]@{ Cond = $name; Clip = $clip.BaseName; Rep = $rep; Status = $r.Status; Ms = $r.Ms; Verdict = $verdict
                                   AudioS = [Math]::Round((WavSeconds $wav), 2); UsageS = $r.Json.usage.prompt_audio_seconds; Text = $text })
    }
  }
}

if (-not $Only -or $Only -contains 'long') {
  $wav = [IO.File]::ReadAllBytes($long.FullName)
  "`n-- $($long.BaseName) ($([Math]::Round((WavSeconds $wav), 1)) s), shipped request only"
  $r = Send $wav $base $shared
  $text = if ($r.Status -eq 200) { [string]$r.Json.text } else { "<HTTP $($r.Status): $($r.Body)>" }
  $h = ([regex]::Matches($text, '(?i)\bhematochezia\b')).Count; $u = ([regex]::Matches($text, '(?i)\bureterolithiasis\b')).Count
  "   list     {0,5} ms  hematochezia {1}/6, ureterolithiasis {2}/3, {3} chars, usage {4}" -f $r.Ms, $h, $u, $text.Length, ($r.Json.usage | ConvertTo-Json -Compress)
  $rows.Add([pscustomobject]@{ Cond = 'list'; Clip = $long.BaseName; Rep = 1; Status = $r.Status; Ms = $r.Ms; Verdict = "h$h/6 u$u/3"
                               AudioS = [Math]::Round((WavSeconds $wav), 2); UsageS = $r.Json.usage.prompt_audio_seconds; Text = $text })
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
