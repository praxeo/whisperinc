<#
  _join_clips.ps1 - builds longer takes out of the six clips in .\clips\, into
  .\joined\ (git-ignored, like every WAV under _scratch), to test how a model
  uses the rest of a take:
    all_six_joined     the six sentences in one 32 s take
    all_six_reversed   the same, last sentence first
    neutral_then_<x>   the neutral sentence, then a clip models tend to miss
  The PCM is joined as-is, so the clips must share one format.

  Produced the 2026-09-24 longer-take rows in CLAUDE.md 4.3.

  USAGE    pwsh .\_join_clips.ps1
           pwsh .\_local_bias_ab.ps1 -Only qwen3,3b -Extra .\joined
#>
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
