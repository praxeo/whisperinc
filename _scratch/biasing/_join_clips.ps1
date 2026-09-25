<#
  _join_clips.ps1 - builds longer takes out of the six clips in .\clips\, to
  test how a model uses the rest of a take. Into .\joined\ (git-ignored, like
  every WAV under _scratch), in the clips' own format:
    all_six_joined     the six sentences in one 32 s take
    all_six_reversed   the same, last sentence first
    neutral_then_<x>   the neutral sentence, then a clip models tend to miss

  With -Tails, also into .\tails\, at 16 kHz mono as the app records, padded
  with real room tone from the owner's mic (by default the takes the silence
  gate kept in %APPDATA%\.WhisperInk\unsent\):
    <clip>+tail3s          one sentence, then 3 s of the key held
    five+tail<N>s          27.6 s of speech, then 2, 5 or 12 s of room tone,
                           so the server's 30 s cut lands in the silence
    all_six_paused+tail3s  the six with 1.5 s pauses (44 s)
    long_<N>x_six          the six N times over, 0.8 s pauses (111-257 s)
    straddle60+tail<N>s    speech to ~55 s, released at 57.4 or 61.4 s
    room_tone_only         5 s of room tone
  Speech-LLMs answer a piece of pure silence with their prompt: Qwen3 recited
  the whole bias list. See CLAUDE.md 4.3 and 10.2.

  Produced the 2026-09-24 longer-take and recited-list rows in CLAUDE.md 4.3.

  USAGE    pwsh .\_join_clips.ps1 [-Tails] [-RoomTone <wav>,<wav>]
           pwsh .\_local_bias_ab.ps1 -Only qwen3,3b -Extra .\joined
           pwsh .\_local_bias_ab.ps1 -Only qwen3 -Extra .\tails [-Fields chunk_seconds=30]
           cd ..\crisp-harness; dotnet run -c Release -- qwen-live   # the app's own code path
#>
param([switch]$Tails, [string[]]$RoomTone)
$ErrorActionPreference = 'Stop'
$src = Join-Path $PSScriptRoot 'clips'
$dst = Join-Path $PSScriptRoot 'joined'
New-Item -ItemType Directory -Force $dst | Out-Null

function Join-Takes([string]$name, [string[]]$parts) {
  $pcm = New-Object IO.MemoryStream
  $fmt = $null; $fmtKey = $null
  foreach ($part in $parts) {
    $b = [IO.File]::ReadAllBytes((Join-Path $src "$part.wav"))
    $i = 12; $found = $false
    while ($i -le $b.Length - 8) {
      $id  = [Text.Encoding]::ASCII.GetString($b, $i, 4)
      $len = [int][BitConverter]::ToUInt32($b, $i + 4)
      if ($id -eq 'fmt ') {
        $key = [BitConverter]::ToString($b, $i + 8, 16)
        if ($fmtKey -and $key -ne $fmtKey) { throw "$part.wav is in a different format from the clips before it" }
        $fmtKey = $key; $fmt = New-Object byte[] 16; [Array]::Copy($b, $i + 8, $fmt, 0, 16)
      }
      if ($id -eq 'data') { $pcm.Write($b, $i + 8, $len); $found = $true; break }
      $i += 8 + $len + ($len % 2)
    }
    if (-not $found) { throw "$part.wav has no data chunk" }
  }
  $data = $pcm.ToArray()
  $out = New-Object IO.MemoryStream
  $w = New-Object IO.BinaryWriter($out)
  $w.Write([Text.Encoding]::ASCII.GetBytes('RIFF')); $w.Write([uint32](36 + $data.Length)); $w.Write([Text.Encoding]::ASCII.GetBytes('WAVE'))
  $w.Write([Text.Encoding]::ASCII.GetBytes('fmt ')); $w.Write([uint32]16); $w.Write($fmt)
  $w.Write([Text.Encoding]::ASCII.GetBytes('data')); $w.Write([uint32]$data.Length); $w.Write($data)
  $w.Flush()
  [IO.File]::WriteAllBytes((Join-Path $dst "$name.wav"), $out.ToArray())
  '{0,-34} {1,5:N1} s' -f "$name.wav", ($data.Length / [BitConverter]::ToUInt32($fmt, 8))
}

$six = 'hematochezia_1', 'hematochezia_2', 'ureterolithiasis', 'ureteral_colic', 'biliary_colic', 'neutral'
Join-Takes 'all_six_joined'   $six
Join-Takes 'all_six_reversed' ($six[($six.Count - 1)..0])
foreach ($x in 'ureterolithiasis', 'ureteral_colic', 'hematochezia_1') { Join-Takes "neutral_then_$x" @('neutral', $x) }
if (-not $Tails) { return }

Add-Type -TypeDefinition @'
using System; using System.IO; using System.Collections.Generic;
public static class Wav16 {
    // PCM16 samples averaged to mono, and the sample rate.
    public static short[] Read(string path, out int rate) {
        byte[] b = File.ReadAllBytes(path); int i = 12, ch = 1, bits = 16; rate = 0;
        while (i + 8 <= b.Length) {
            string id = System.Text.Encoding.ASCII.GetString(b, i, 4); int len = BitConverter.ToInt32(b, i + 4);
            if (id == "fmt ") { ch = BitConverter.ToInt16(b, i + 10); rate = BitConverter.ToInt32(b, i + 12); bits = BitConverter.ToInt16(b, i + 22); }
            if (id == "data") {
                if (bits != 16) throw new Exception(path + " is not 16-bit PCM");
                int n = len / 2 / ch; var s = new short[n];
                for (int k = 0; k < n; k++) { int acc = 0; for (int c = 0; c < ch; c++) acc += BitConverter.ToInt16(b, i + 8 + (k * ch + c) * 2); s[k] = (short)(acc / ch); }
                return s;
            }
            i += 8 + len + (len & 1);
        }
        throw new Exception(path + " has no data chunk");
    }
    // 48 kHz to 16 kHz: a 97-tap Blackman-windowed sinc low-pass at 7.2 kHz, then every third sample.
    public static short[] Down3(short[] x) {
        const int taps = 97; int m = taps / 2; double fc = 7200.0 / 48000.0; var h = new double[taps]; double sum = 0;
        for (int n = 0; n < taps; n++) {
            double t = n - m; double sinc = t == 0 ? 2 * fc : Math.Sin(2 * Math.PI * fc * t) / (Math.PI * t);
            double w = 0.42 - 0.5 * Math.Cos(2 * Math.PI * n / (taps - 1)) + 0.08 * Math.Cos(4 * Math.PI * n / (taps - 1));
            h[n] = sinc * w; sum += h[n];
        }
        for (int n = 0; n < taps; n++) h[n] /= sum;
        var y = new short[x.Length / 3];
        for (int k = 0; k < y.Length; k++) {
            double acc = 0;
            for (int n = 0; n < taps; n++) { int j = 3 * k + n - m; if (j >= 0 && j < x.Length) acc += h[n] * x[j]; }
            y[k] = (short)Math.Max(-32768, Math.Min(32767, Math.Round(acc)));
        }
        return y;
    }
    public static double Write(string path, IEnumerable<short[]> parts) {
        var all = new List<short>(); foreach (var p in parts) all.AddRange(p);
        using (var fs = File.Create(path)) using (var w = new BinaryWriter(fs)) {
            int bytes = all.Count * 2;
            w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + bytes); w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
            w.Write(System.Text.Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(16000); w.Write(32000); w.Write((short)2); w.Write((short)16);
            w.Write(System.Text.Encoding.ASCII.GetBytes("data")); w.Write(bytes); foreach (var s in all) w.Write(s);
        }
        return all.Count / 16000.0;
    }
}
'@

$tdst = Join-Path $PSScriptRoot 'tails'
New-Item -ItemType Directory -Force $tdst | Out-Null
$rate = 0
$clip = @{}
foreach ($n in $six) {
  $s = [Wav16]::Read((Join-Path $src "$n.wav"), [ref]$rate)
  $clip[$n] = if ($rate -eq 48000) { [Wav16]::Down3($s) } elseif ($rate -eq 16000) { $s } else { throw "$n.wav is $rate Hz; only 48 kHz and 16 kHz are handled" }
}
if (-not $RoomTone) {
  $unsent = Join-Path $env:APPDATA '.WhisperInk\unsent'
  $RoomTone = @(Get-ChildItem (Join-Path $unsent '*.json') -ErrorAction SilentlyContinue |
                Where-Object { (Get-Content $_.FullName -Raw | ConvertFrom-Json).Status -eq 'quiet' } |
                ForEach-Object { [IO.Path]::ChangeExtension($_.FullName, '.wav') } | Where-Object { Test-Path $_ })
  if (-not $RoomTone) { throw "no room tone: no takes judged silent in $unsent; pass -RoomTone <16 kHz wav>" }
}
$RoomTone = @($RoomTone | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$room = New-Object System.Collections.Generic.List[short]
foreach ($r in $RoomTone) { $t = [Wav16]::Read($r, [ref]$rate); if ($rate -ne 16000) { throw "$r is $rate Hz; room tone must be 16 kHz" }; $room.AddRange($t) }
'room tone: {0:N1} s from {1} file(s)' -f ($room.Count / 16000.0), $RoomTone.Count
# $sec of room tone, starting $from samples in, wrapping round.
function Tone([double]$sec, [int]$from = 0) { $n = [int]($sec * 16000); $o = New-Object short[] $n; for ($i = 0; $i -lt $n; $i++) { $o[$i] = $room[($from + $i) % $room.Count] }; ,$o }
function Save([string]$name, $parts) { '{0,-34} {1,5:N1} s' -f "$name.wav", [Wav16]::Write((Join-Path $tdst "$name.wav"), [short[][]]$parts) }

foreach ($n in $six) { Save "$n+tail3s" @($clip[$n], (Tone 3)) }
$five = @($six[0..4] | ForEach-Object { $clip[$_] })
foreach ($tail in 2, 5, 12) { Save "five+tail${tail}s" ($five + ,(Tone $tail 3000)) }
$paused = @(); $k = 0
foreach ($n in $six) { $paused += ,$clip[$n]; $paused += ,(Tone 1.5 ($k * 24000)); $k++ }
Save 'all_six_paused+tail3s' ($paused + ,(Tone 3 7000))
foreach ($reps in 3..7) {
  $long = @(); $k = 0
  foreach ($r in 1..$reps) { foreach ($n in $six) { $long += ,$clip[$n]; $long += ,(Tone 0.8 ($k * 12800)); $k++ } }
  Save "long_${reps}x_six" ($long + ,(Tone 1.5 5000))
}
# Released past the 60 s mark after the speech stopped at ~55 s: the one
# case the server's 60 s cut still turned into a piece of silence.
$st = @(); $k = 0
foreach ($n in $six) { $st += ,$clip[$n]; $st += ,(Tone 0.8 ($k * 12800)); $k++ }
foreach ($n in $six[0..2]) { $st += ,$clip[$n]; if ($n -ne $six[2]) { $st += ,(Tone 0.8 ($k * 12800)); $k++ } }
foreach ($tail in 2, 6) { Save "straddle60+tail${tail}s" ($st + ,(Tone $tail 9000)) }
Save 'room_tone_only' @(,(Tone 5))
