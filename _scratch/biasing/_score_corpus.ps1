#requires -Version 7
<#
  _score_corpus.ps1 - score _local_bias_ab.ps1 output on the real-dictation corpus.

  The corpus is %APPDATA%\.WhisperInk\ab-corpus\ (real clinical dictations, so it is outside the repo and
  OneDrive): wav\ and manifest.json, whose "reference" is the text the owner checked against what they said.
  Run the A/B on it, then score the CSV. Keep the CSV and the console log under %APPDATA% too, they hold
  the transcripts:

    pwsh .\_local_bias_ab.ps1 -Extra "$env:APPDATA\.WhisperInk\ab-corpus\wav" -Csv <dir>\corpus.csv -Label corpus-
    pwsh .\_score_corpus.ps1 -Csv <dir>\corpus.csv

  Per run and condition (none = no hotwords; BIAS = the shared list, as WhisperInk sends it):
    WER       (substitutions + deletions + insertions) / reference words, over the whole corpus, after both
              sides are normalized: lower case, no punctuation, hyphens as spaces, "G.I." as "GI", spelled-out
              numbers as digits. So "Mallory-Weiss" = "mallory weiss" and "eighteen" = "18". Everything else,
              a spelling, a dropped word, "Mag" for "magnesium", counts.
    perfect   clips with no word error at all.
    listed / other
              the manifest's focus terms, split into those on the shared list and those on neither. Each
              occurrence in the reference is one chance; it is a hit only when the alignment matches all of
              its words in place with nothing inserted between them.
    bad       transcripts that are empty, an <error: ...>, hold <unk> or recite the list (LIST?). They are
              scored for what they are (an empty one is all deletions) and counted here.
    6 clips   the six scripted clips' own terms, read the way _ab_compare.ps1 -Score reads them. It is the
              anchor: a run that disagrees with the older tables in CLAUDE.md is a run to distrust.
    warn      (only when a CSV has a Warning column, as corpus-cloud's does) takes the provider itself
              flagged, e.g. Omi saying it did not use the term list.
    ms        median request time. A cloud run's includes the network.
  Greedy decoding makes the two reps identical; rep 1 is scored, and the header says how many pairs differ.
  Rows for clips in neither the manifest nor the six are ignored.

  USAGE  pwsh .\_score_corpus.ps1 -Csv corpus.csv
         pwsh .\_score_corpus.ps1 -Csv corpus.csv -Misses          # each missed focus term and what was written instead
         pwsh .\_score_corpus.ps1 -Csv corpus.csv -Diffs -Only qwen3   # every word error, for runs whose name contains this
         pwsh .\_score_corpus.ps1 -Csv corpus.csv -Grid             # focus term x run, one grid per condition
         pwsh .\_score_corpus.ps1 -Csv corpus.csv -Out scored.csv   # one metrics row per run, condition, clip and rep
         pwsh .\_score_corpus.ps1 -SelfTest                         # checks the scorer itself, no data needed
#>
param(
  [string[]]$Csv,
  [string]$Manifest = (Join-Path $env:APPDATA '.WhisperInk\ab-corpus\manifest.json'),
  [string[]]$Only,
  [string]$Out,
  [switch]$Misses,
  [switch]$Diffs,
  [switch]$Grid,
  [switch]$Units,
  [switch]$SplitScribe,
  [switch]$BySet,
  [switch]$SelfTest
)
$ErrorActionPreference = 'Stop'
# `pwsh -File` hands "a,b" over as one string, so these are split here too.
$Csv  = @($Csv  | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$Only = @($Only | ForEach-Object { $_ -split ',' } | Where-Object { $_ })

# ---- normalization -------------------------------------------------------------------------------------------
$OneWords = @{ zero = 0; one = 1; two = 2; three = 3; four = 4; five = 5; six = 6; seven = 7; eight = 8; nine = 9; ten = 10
               eleven = 11; twelve = 12; thirteen = 13; fourteen = 14; fifteen = 15; sixteen = 16; seventeen = 17
               eighteen = 18; nineteen = 19 }
$TenWords = @{ twenty = 20; thirty = 30; forty = 40; fifty = 50; sixty = 60; seventy = 70; eighty = 80; ninety = 90 }

# Folds spelled-out numbers up to 999 into digits: "eighteen" -> 18, "six hundred" -> 600, "twenty one" -> 21.
# It runs on both sides, so an over-eager fold ("one month") costs nothing.
function Fold-Numbers([System.Collections.Generic.List[string]]$w) {
  $res = [System.Collections.Generic.List[string]]::new()
  $i = 0
  while ($i -lt $w.Count) {
    $j = $i; $val = 0
    if ($j + 1 -lt $w.Count -and $OneWords.ContainsKey($w[$j]) -and $OneWords[$w[$j]] -ge 1 -and $OneWords[$w[$j]] -le 9 -and $w[$j + 1] -ceq 'hundred') {
      $val = 100 * $OneWords[$w[$j]]; $j += 2
    }
    if ($j -lt $w.Count -and $TenWords.ContainsKey($w[$j])) {
      $val += $TenWords[$w[$j]]; $j++
      if ($j -lt $w.Count -and $OneWords.ContainsKey($w[$j]) -and $OneWords[$w[$j]] -ge 1 -and $OneWords[$w[$j]] -le 9) { $val += $OneWords[$w[$j]]; $j++ }
    } elseif ($j -lt $w.Count -and $OneWords.ContainsKey($w[$j])) {
      $val += $OneWords[$w[$j]]; $j++
    }
    if ($j -gt $i) { $res.Add("$val"); $i = $j } else { $res.Add($w[$i]); $i++ }
  }
  return , $res
}

function Get-Tokens([string]$text) {
  $w = [System.Collections.Generic.List[string]]::new()
  if ([string]::IsNullOrWhiteSpace($text)) { return , $w }
  $t = $text.ToLowerInvariant().Replace([string][char]0x2019, "'")
  # dotted abbreviations: "g.i." -> "gi"
  $t = [regex]::Replace($t, "(?<![\p{L}\p{N}])(?:\p{L}\.){2,}", [System.Text.RegularExpressions.MatchEvaluator]{ param($m) $m.Value.Replace('.', '') })
  foreach ($m in [regex]::Matches($t, "[\p{L}\p{N}']+(?:[.,]\p{N}+)*")) {
    $s = $m.Value.Trim("'")
    if ($Units) {
      $s = $s.Replace("'", '')                                                   # dakin's = dakins, o'clock = oclock
      if ($s -match '^\d{1,3}(,\d{3})+(\.\d+)?$') { $s = $s.Replace(',', '') }   # 1,000 = 1000
    }
    if ($s) { $w.Add($s) }
  }
  $w = Fold-Numbers $w
  if ($Units) { $w = Fold-Units $w }
  return , $w
}

# -Units: the formats a transcriber may choose between for the same words. Off by default so the numbers already
# published for the first corpus stay reproducible. Folds units (milligrams = mg, minutes = min, millimeters of
# mercury = mmHg), drops "percent", drops "by", "x" and "over" between two numbers (3.2 by 2.1 = 3.2 x 2.1,
# 96 over 58 = 96/58), drops "per" beside a unit (5 mcg per minute = 5 mcg/min) and "plus" after a number
# (2 plus = 2+), reads II and III as 2 and 3, and joins "non" to the next word (non-palpable = nonpalpable).
$UnitMap = @{ milligram = 'mg'; milligrams = 'mg'; microgram = 'mcg'; micrograms = 'mcg'; gram = 'g'; grams = 'g'; kilogram = 'kg'; kilograms = 'kg'
              milliliter = 'ml'; milliliters = 'ml'; millilitre = 'ml'; millilitres = 'ml'; liter = 'l'; liters = 'l'; litre = 'l'; litres = 'l'
              centimeter = 'cm'; centimeters = 'cm'; millimeter = 'mm'; millimeters = 'mm'; gigabyte = 'gb'; gigabytes = 'gb'
              minute = 'min'; minutes = 'min'; hour = 'hr'; hours = 'hr'; ccs = 'cc' }
$PerUnits = @('mg', 'mcg', 'g', 'kg', 'ml', 'l', 'cc', 'unit', 'units', 'meq', 'mmol', 'min', 'hr')
function Test-Num([string]$s) { $s -match '^\d+(?:[.,]\d+)?$' }
function Fold-Units([System.Collections.Generic.List[string]]$w) {
  $a = [System.Collections.Generic.List[string]]::new(); $i = 0
  while ($i -lt $w.Count) {
    if ($i + 2 -lt $w.Count -and ($w[$i] -ceq 'millimeters' -or $w[$i] -ceq 'millimeter') -and $w[$i + 1] -ceq 'of' -and $w[$i + 2] -ceq 'mercury') { $a.Add('mmhg'); $i += 3; continue }
    if ($i + 1 -lt $w.Count -and $w[$i] -ceq 'mm' -and $w[$i + 1] -ceq 'hg') { $a.Add('mmhg'); $i += 2; continue }
    $a.Add($w[$i]); $i++
  }
  # spoken decimals: "three point two" (3 point 2) = 3.2, "point four" = 0.4. "point" anywhere else is left alone.
  $d = [System.Collections.Generic.List[string]]::new(); $k = 0
  while ($k -lt $a.Count) {
    if ($a[$k] -ceq 'point' -and $k + 1 -lt $a.Count -and $a[$k + 1] -match '^\d+$') {
      if ($d.Count -gt 0 -and $d[$d.Count - 1] -match '^\d+$') { $d[$d.Count - 1] = $d[$d.Count - 1] + '.' + $a[$k + 1] } else { $d.Add('0.' + $a[$k + 1]) }
      $k += 2
    } else { $d.Add($a[$k]); $k++ }
  }
  $a = $d
  for ($k = 0; $k -lt $a.Count; $k++) {
    $t = $a[$k]
    if ($UnitMap.ContainsKey($t)) { $a[$k] = $UnitMap[$t] }
    elseif ($t -ceq 'iii') { $a[$k] = '3' } elseif ($t -ceq 'ii') { $a[$k] = '2' }
  }
  $r = [System.Collections.Generic.List[string]]::new()
  for ($k = 0; $k -lt $a.Count; $k++) {
    $t = $a[$k]
    $prev = if ($k -gt 0) { $a[$k - 1] } else { '' }
    $next = if ($k + 1 -lt $a.Count) { $a[$k + 1] } else { '' }
    if ($t -ceq 'percent') { continue }
    if (($t -ceq 'by' -or $t -ceq 'x' -or $t -ceq 'over') -and (Test-Num $prev) -and (Test-Num $next)) { continue }
    if ($t -ceq 'per' -and ($PerUnits -contains $prev -or $PerUnits -contains $next)) { continue }
    if ($t -ceq 'plus' -and (Test-Num $prev)) { continue }
    $r.Add($t)
  }
  $m = [System.Collections.Generic.List[string]]::new(); $k = 0
  while ($k -lt $r.Count) {
    if ($r[$k] -ceq 'non' -and $k + 1 -lt $r.Count) { $m.Add('non' + $r[$k + 1]); $k += 2 } else { $m.Add($r[$k]); $k++ }
  }
  return , $m
}

# ---- alignment -----------------------------------------------------------------------------------------------
# Word-level edit distance with a backtrace. Ops, in order: T = ok | sub | del | ins, R = the reference index (for
# an insertion: the reference word it sits in front of), W = the reference word, H = the hypothesis word.
function Align([System.Collections.Generic.List[string]]$ref, [System.Collections.Generic.List[string]]$hyp) {
  $n = $ref.Count; $m = $hyp.Count
  $d = [int[,]]::new($n + 1, $m + 1)
  for ($a = 0; $a -le $n; $a++) { $d[$a, 0] = $a }
  for ($b = 0; $b -le $m; $b++) { $d[0, $b] = $b }
  for ($a = 1; $a -le $n; $a++) {
    for ($b = 1; $b -le $m; $b++) {
      $viaSub = $d[($a - 1), ($b - 1)] + $(if ($ref[$a - 1] -ceq $hyp[$b - 1]) { 0 } else { 1 })
      $viaDel = $d[($a - 1), $b] + 1
      $viaIns = $d[$a, ($b - 1)] + 1
      $d[$a, $b] = [Math]::Min($viaSub, [Math]::Min($viaDel, $viaIns))
    }
  }
  $ops = [System.Collections.Generic.List[object]]::new()
  $a = $n; $b = $m
  while ($a -gt 0 -or $b -gt 0) {
    if ($a -gt 0 -and $b -gt 0) {
      $same = $ref[$a - 1] -ceq $hyp[$b - 1]
      if ($d[$a, $b] -eq $d[($a - 1), ($b - 1)] + $(if ($same) { 0 } else { 1 })) {
        $ops.Add([pscustomobject]@{ T = $(if ($same) { 'ok' } else { 'sub' }); R = $a - 1; W = $ref[$a - 1]; H = $hyp[$b - 1] })
        $a--; $b--; continue
      }
    }
    if ($a -gt 0 -and $d[$a, $b] -eq $d[($a - 1), $b] + 1) {
      $ops.Add([pscustomobject]@{ T = 'del'; R = $a - 1; W = $ref[$a - 1]; H = '' }); $a--; continue
    }
    $ops.Add([pscustomobject]@{ T = 'ins'; R = $a; W = ''; H = $hyp[$b - 1] }); $b--
  }
  $ops.Reverse()
  return , $ops
}

function Find-Sub([System.Collections.Generic.List[string]]$hay, [System.Collections.Generic.List[string]]$needle) {
  $at = [System.Collections.Generic.List[int]]::new()
  if ($needle.Count -eq 0) { return , $at }
  for ($a = 0; $a -le $hay.Count - $needle.Count; $a++) {
    $ok = $true
    for ($k = 0; $k -lt $needle.Count; $k++) { if ($hay[$a + $k] -cne $needle[$k]) { $ok = $false; break } }
    if ($ok) { $at.Add($a) }
  }
  return , $at
}

# A clip as the scorer holds it: the reference words, and where each focus term sits in them.
function New-Clip([string]$name, [string]$reference, $focus) {
  $ref = Get-Tokens $reference
  $terms = [System.Collections.Generic.List[object]]::new()
  foreach ($f in @($focus)) {
    $tok = Get-Tokens $f.term
    $terms.Add([pscustomobject]@{ Text = $f.term; Listed = [bool]$f.onSharedList; Scribe = ([bool]$f.onScribeList -and -not [bool]$f.onSharedList); Tok = $tok; Starts = (Find-Sub $ref $tok) })
  }
  [pscustomobject]@{ Name = $name; Ref = $ref; Terms = $terms; RefText = $reference }
}

$cache = @{}
function Score-Text($clip, [string]$text) {
  $key = $clip.Name + "`n" + $text
  if ($cache.ContainsKey($key)) { return $cache[$key] }
  $hyp = if ($text -like '<error*') { Get-Tokens '' } else { Get-Tokens $text }
  $ops = Align $clip.Ref $hyp
  $nSub = 0; $nDel = 0; $nIns = 0
  foreach ($o in $ops) { if ($o.T -eq 'sub') { $nSub++ } elseif ($o.T -eq 'del') { $nDel++ } elseif ($o.T -eq 'ins') { $nIns++ } }
  $chances = [System.Collections.Generic.List[object]]::new()
  foreach ($t in $clip.Terms) {
    foreach ($from in $t.Starts) {
      $to = $from + $t.Tok.Count
      $hit = $true
      $heard = [System.Collections.Generic.List[string]]::new()
      $heardNear = [System.Collections.Generic.List[string]]::new()   # the same, plus words inserted right beside the term
      foreach ($o in $ops) {
        if ($o.T -ne 'ins' -and $o.R -ge $from -and $o.R -lt $to) {
          if ($o.T -ne 'ok') { $hit = $false }
          $w = $(if ($o.T -eq 'del') { '(dropped)' } else { $o.H })
          $heard.Add($w); $heardNear.Add($w)
        } elseif ($o.T -eq 'ins' -and $o.R -gt $from -and $o.R -lt $to) {   # a word inserted between the term's words
          $hit = $false; $heard.Add($o.H); $heardNear.Add($o.H)
        } elseif ($o.T -eq 'ins' -and ($o.R -eq $from -or $o.R -eq $to)) {  # beside it: "pantoprazole" heard as "panto prazole"
          $heardNear.Add('+' + $o.H)
        }
      }
      # beside-the-term insertions only matter when the term was missed (then they are often its other half)
      $chances.Add([pscustomobject]@{ Term = $t.Text; Listed = $t.Listed; Scribe = $t.Scribe; Hit = $hit; Heard = $(if ($hit) { $heard -join ' ' } else { $heardNear -join ' ' }) })
    }
  }
  $res = [pscustomobject]@{ N = $clip.Ref.Count; S = $nSub; D = $nDel; I = $nIns; Ops = $ops; Chances = $chances }
  $cache[$key] = $res
  return $res
}

function Median($v) {
  $s = @($v | Sort-Object); $c = $s.Count
  if ($c -eq 0) { return [double]::NaN }
  if ($c % 2) { return $s[($c - 1) / 2] }
  return ($s[$c / 2 - 1] + $s[$c / 2]) / 2
}

# ---- self-test -----------------------------------------------------------------------------------------------
if ($SelfTest) {
  $checks = 0; $failed = 0
  function Check([string]$what, $got, $want) {
    $script:checks++
    if ("$got" -ceq "$want") { return }
    $script:failed++; "FAIL: $what`n   got:  $got`n   want: $want"
  }
  function Tok([string]$s) { (Get-Tokens $s) -join ' ' }
  function Counts($clip, [string]$text) { $r = Score-Text $clip $text; "S$($r.S) D$($r.D) I$($r.I)" }
  function Terms($clip, [string]$text) { $r = Score-Text $clip $text; (@($r.Chances | ForEach-Object { if ($_.Hit) { 'hit' } else { "miss:$($_.Heard)" } })) -join ',' }

  Check 'case, hyphen, final period' (Tok 'Mallory-Weiss syndrome.') 'mallory weiss syndrome'
  Check 'dotted abbreviations' (Tok 'G.I. ulcer, D.D.X. today') 'gi ulcer ddx today'
  Check 'a sentence-ending period is not an abbreviation' (Tok 'It is here.Then more.') 'it is here then more'
  Check 'a number as digits' (Tok 'An 18-year-old male') 'an 18 year old male'
  Check 'a number as words' (Tok 'An eighteen-year-old male') 'an 18 year old male'
  Check 'hundreds' (Tok 'six hundred cc') '600 cc'
  Check 'tens and ones' (Tok 'twenty-one, fifty') '21 50'
  Check 'hundreds, tens and ones' (Tok 'one hundred five and two hundred forty seven') '105 and 247'
  Check 'a decimal stays whole' (Tok 'It was 98.6 degrees') 'it was 98.6 degrees'
  Check 'an apostrophe inside a word stays' (Tok "we'll provide 'it'") "we'll provide it"
  Check 'a curly apostrophe' (Tok "we`u{2019}ll") "we'll"
  Check 'nothing at all' (Tok '') ''
  Check 'only punctuation' (Tok '... !') ''

  $c1 = New-Clip 't1' 'a b c d' @()
  Check 'one substitution and one deletion' (Counts $c1 'a x c') 'S1 D1 I0'
  Check 'an insertion' (Counts (New-Clip 't2' 'a b' @()) 'a x b') 'S0 D0 I1'
  Check 'an empty transcript is all deletions' (Counts (New-Clip 't3' 'a b c' @()) '') 'S0 D3 I0'
  Check 'an <error> row is all deletions' (Counts (New-Clip 't4' 'a b c' @()) '<error: HTTP 500>') 'S0 D3 I0'
  Check 'identical after normalization' (Counts (New-Clip 't5' 'Six hundred cc out.' @()) '600 CC out') 'S0 D0 I0'
  Check 'a made-up word is one substitution' (Counts (New-Clip 't6' 'give lactulose daily' @()) 'give lactulos daily') 'S1 D0 I0'

  $foley = New-Clip 'f' 'take a foley now' @(@{ term = 'Foley'; onSharedList = $false })
  Check 'a term in place' (Terms $foley 'Take a Foley now.') 'hit'
  Check 'a term misheard' (Terms $foley 'take a foil now') 'miss:foil'
  Check 'a term dropped' (Terms $foley 'take a now') 'miss:(dropped)'
  Check 'a term one word late is still a term in place' (Terms $foley 'so take a foley now') 'hit'
  $mw = New-Clip 'm' 'consistent with mallory-weiss tears' @(@{ term = 'Mallory-Weiss'; onSharedList = $false })
  Check 'a hyphenated term written with a space' (Terms $mw 'consistent with Mallory Weiss tears') 'hit'
  Check 'a hyphenated term misspelled' (Terms $mw 'consistent with malory weiss tears') 'miss:malory weiss'
  Check 'a word inserted inside a two-word term' (Terms $mw 'consistent with mallory and weiss tears') 'miss:mallory and weiss'
  Check 'a word inserted beside a term is fine' (Terms $mw 'consistent with the mallory weiss tears') 'hit'
  $pp = New-Clip 'p' 'give pantoprazole daily' @(@{ term = 'pantoprazole'; onSharedList = $false })
  Check 'a term split into two words is a miss that shows both halves' ((Terms $pp 'give panto prazole daily') -replace '\+', '') 'miss:panto prazole'
  Check 'an extra word beside a correct term leaves it a hit' (Terms $foley 'take a foley and now') 'hit'
  $two = New-Clip 'h' 'hematemesis today hematemesis' @(@{ term = 'hematemesis'; onSharedList = $true })
  Check 'two chances, one hit' (Terms $two 'hematemesis today hematomesis') 'hit,miss:hematomesis'
  Check 'two chances, both hit' (Terms $two 'Hematemesis today, hematemesis.') 'hit,hit'
  $anyClip = New-Clip 'x' 'Start a step-up plan, drain 500 cc, no G.I. sign.' @()
  Check 'a reference scored against itself' (Counts $anyClip 'Start a step-up plan, drain 500 cc, no G.I. sign.') 'S0 D0 I0'

  # -Units: the extra folding, checked with the switch on (the checks above ran with it off, as published).
  $Units = $true; $cache.Clear()
  Check 'units: milligrams = mg' (Tok '4 milligrams IV') '4 mg iv'
  Check 'units: a liter is an L' (Tok 'a 1 liter bolus') (Tok 'a 1 L bolus')
  Check 'units: minutes = min' (Tok '10 minutes') '10 min'
  Check 'units: ccs = cc' (Tok "600 cc's") '600 cc'
  Check 'units: gigabytes = GB' (Tok '24 gigabytes of VRAM') (Tok '24 GB of VRAM')
  Check 'percent: 60% = 60 percent' (Tok '60% granulation') (Tok '60 percent granulation')
  Check 'percent: the sign is dropped' (Tok '60% granulation') '60 granulation'
  Check 'by, x and the multiplication sign between numbers' (Tok '3.2 by 2.1 by 0.4 cm') '3.2 2.1 0.4 cm'
  Check 'x between numbers' (Tok '3.2 x 2.1 x 0.4 cm') '3.2 2.1 0.4 cm'
  Check 'the multiplication sign' (Tok ('3.2 ' + [char]0xD7 + ' 2.1 ' + [char]0xD7 + ' 0.4 centimeters')) '3.2 2.1 0.4 cm'
  Check 'by is kept between words' (Tok 'followed by a nurse') 'followed by a nurse'
  Check 'over between numbers' (Tok '96 over 58') '96 58'
  Check 'a slash between numbers' (Tok '96/58') '96 58'
  Check 'per beside a unit' (Tok '5 mcg per minute') '5 mcg min'
  Check 'a slash between units' (Tok '5 mcg/min') '5 mcg min'
  Check 'mg per kg' (Tok '1 mg per kg') (Tok '1 mg/kg')
  Check 'per is kept elsewhere' (Tok 'vancomycin per pharmacy') 'vancomycin per pharmacy'
  Check 'plus after a number' (Tok '2 plus bilaterally') '2 bilaterally'
  Check 'a plus sign after a number' (Tok '2+ bilaterally') '2 bilaterally'
  Check 'millimeters of mercury' (Tok '125 millimeters of mercury') '125 mmhg'
  Check 'mmHg' (Tok '125 mmHg') '125 mmhg'
  Check 'mm Hg' (Tok '125 mm Hg') '125 mmhg'
  Check 'Roman III is 3' (Tok 'Wagner grade III') 'wagner grade 3'
  Check 'IV stays IV' (Tok '4 mg IV') '4 mg iv'
  Check 'non joined to the next word' (Tok 'non-palpable') 'nonpalpable'
  Check 'nonpalpable as one word' (Tok 'nonpalpable') 'nonpalpable'
  Check 'an apostrophe inside a word goes' (Tok "Dakin's") 'dakins'
  Check 'o''clock' (Tok "12 o'clock") '12 oclock'
  Check 'a thousands comma' (Tok '1,000 mg') '1000 mg'
  Check 'a decimal stays whole' (Tok '98.6 degrees') '98.6 degrees'
  Check 'a spoken decimal' (Tok 'three point two by two point one') '3.2 2.1'
  Check 'a spoken decimal with no leading zero' (Tok 'point four centimeters') '0.4 cm'
  Check 'a spoken decimal equals the written one' (Tok 'zero point five mg') (Tok '0.5 mg')
  Check 'point elsewhere is left alone' (Tok 'the point of care') 'the point of care'
  $bp =New-Clip 'v' 'blood pressure 96 over 58, 4 milligrams IV' @()
  Check 'a whole line, units folded on both sides' (Counts $bp 'Blood pressure 96/58, 4 mg IV') 'S0 D0 I0'
  Check 'a real difference still counts' (Counts $bp 'Blood pressure 96/58, 5 mg IV') 'S1 D0 I0'
  $sc1 = New-Clip 'w' 'apply santyl and algidex ag' @(@{ term = 'Santyl'; onSharedList = $false; onScribeList = $true }, @{ term = 'Algidex Ag'; onSharedList = $true; onScribeList = $true }, @{ term = 'apply'; onSharedList = $false; onScribeList = $false })
  Check 'a term only on the Scribe list' ($sc1.Terms[0].Scribe) 'True'
  Check 'a term on both lists counts as shared' ("$($sc1.Terms[1].Listed) $($sc1.Terms[1].Scribe)") 'True False'
  Check 'a term on neither list' ("$($sc1.Terms[2].Listed) $($sc1.Terms[2].Scribe)") 'False False'
  $Units = $false; $cache.Clear()

  "self-test: $checks checks, $failed failed"
  if ($failed) { exit 1 } else { exit 0 }
}

# ---- load ----------------------------------------------------------------------------------------------------
if (-not $Csv) { throw 'give -Csv <file> (what _local_bias_ab.ps1 -Csv wrote), or -SelfTest' }
if (-not (Test-Path -LiteralPath $Manifest)) { throw "no manifest at $Manifest" }
$man = Get-Content -LiteralPath $Manifest -Raw | ConvertFrom-Json
$clips = @{}
$clipSet = @{}
$nonSpeech = @{}   # takes with no words in them: the right answer is nothing
foreach ($c in $man.clips) {
  $name = [IO.Path]::GetFileNameWithoutExtension($c.file)
  $clipSet[$name] = if ($c.set) { "$($c.set)" } else { $name.Substring(0, 1) }
  if ($c.expectEmpty) { $nonSpeech[$name] = $true; continue }
  if (-not $c.reference) { throw "$($c.file) has no reference in the manifest: fill it in before scoring" }
  $clips[$name] = New-Clip $name $c.reference $c.focusTerms
  foreach ($t in $clips[$name].Terms) { if ($t.Starts.Count -eq 0) { "warning: focus term '$($t.Text)' is not in the reference of $name, so it is not scored" } }
}
$refWords = ($clips.Values | ForEach-Object { $_.Ref.Count } | Measure-Object -Sum).Sum
$chancesL = 0; $chancesS = 0; $chancesO = 0
foreach ($c in $clips.Values) { foreach ($t in $c.Terms) {
  if ($t.Listed) { $chancesL += $t.Starts.Count } elseif ($t.Scribe) { $chancesS += $t.Starts.Count } else { $chancesO += $t.Starts.Count } } }

$targets = [ordered]@{ hematochezia_1 = 'hematochezia'; hematochezia_2 = 'hematochezia'; ureterolithiasis = 'ureterolithiasis'
                       biliary_colic = 'biliary colic'; ureteral_colic = 'ureteral colic'; neutral = 'stable condition' }
$cfg = Get-Content (Join-Path $env:APPDATA '.WhisperInk\config.json') -Raw | ConvertFrom-Json
$listTerms = @($cfg.ContextBiasTerms | Where-Object { $_ })
function Has([string]$text, [string]$phrase) { $text -match ('(?i)(?<!\w)' + [regex]::Escape($phrase) + '(?!\w)') }
function SixOk($row) {
  if (-not (Has $row.Text $targets[$row.Clip])) { return $false }
  if ($row.Clip -ne 'neutral') { return $true }
  foreach ($t in $listTerms) { if (Has $row.Text $t) { return $false } }
  return $true
}

$raw = @($Csv | ForEach-Object { Import-Csv -LiteralPath $_ })
$runNames = @($raw | ForEach-Object Run | Select-Object -Unique)
if ($Only) { $runNames = @($runNames | Where-Object { $rn = $_; @($Only | Where-Object { $rn -like "*$_*" }).Count -gt 0 }) }

$scored = [System.Collections.Generic.List[object]]::new()
$sixRows = [System.Collections.Generic.List[object]]::new()
$ghostRows = [System.Collections.Generic.List[object]]::new()
$serverFailed = @{}
$ignored = 0
$texts = @{}
foreach ($r in $raw) {
  if ($r.Run -notin $runNames) { continue }
  if ($r.Clip -eq '(server)') { $serverFailed[$r.Run] = "$($r.Text)"; continue }
  $text = "$($r.Text)"
  if ($targets.Contains($r.Clip)) { $sixRows.Add([pscustomobject]@{ Run = $r.Run; Bias = $r.Bias; Clip = $r.Clip; Text = $text }); continue }
  if ($nonSpeech.ContainsKey($r.Clip)) {
    $ghostRows.Add([pscustomobject]@{ Run = $r.Run; Bias = $r.Bias; Clip = $r.Clip; Rep = [int]$r.Rep; Ghost = ($text.Trim() -ne '' -and $text -notlike '<error*') })
    continue
  }
  $clip = $clips[$r.Clip]
  if (-not $clip) { $ignored++; continue }
  $sc = Score-Text $clip $text
  # "Recited the list" = four or more list terms the reference does NOT contain (the CSV's own Recited column can't tell
  # a recital from a script that legitimately uses several list terms).
  $inventedTerms = @($listTerms | Where-Object { (Has $text $_) -and -not (Has $clip.RefText $_) }).Count
  $bad = ($text -eq '') -or ($text -like '<error*') -or ($text -match '<unk>') -or ($inventedTerms -ge 4)
  $err = $sc.S + $sc.D + $sc.I
  # A provider's own "check this" flag (the Warning column corpus-cloud writes; Omi's says it dropped the term list).
  $warned = $null -ne $r.PSObject.Properties['Warning'] -and "$($r.Warning)" -ne ''
  $scored.Add([pscustomobject]@{
    Run = $r.Run; Bias = $r.Bias; Clip = $r.Clip; Set = $clipSet[$r.Clip]; Rep = [int]$r.Rep; Ms = [double]$r.Ms; Bad = $bad; Warned = $warned
    N = $sc.N; S = $sc.S; D = $sc.D; I = $sc.I; Err = $err; Perfect = ($err -eq 0)
    Chances = $sc.Chances; Ops = $sc.Ops })
  $k = "$($r.Run)|$($r.Bias)|$($r.Clip)"
  if (-not $texts.ContainsKey($k)) { $texts[$k] = [System.Collections.Generic.HashSet[string]]::new() }
  [void]$texts[$k].Add($text)
}
$differ = @($texts.Values | Where-Object { $_.Count -gt 1 }).Count

$nsNote = if ($nonSpeech.Count) { "; $($nonSpeech.Count) non-speech take(s) checked separately" } else { '' }
$chanceNote = if ($chancesS) { "$chancesL on the shared list, $chancesS only on the Scribe list, $chancesO on neither" } else { "$chancesL on the shared list, $chancesO on neither" }
"corpus: $($clips.Count) clips, $refWords reference words, $($chancesL + $chancesS + $chancesO) focus-term chances ($chanceNote)$nsNote$(if ($Units) { '; units and formats folded (-Units)' })"
"input:  $($raw.Count) rows in $($Csv.Count) file(s), $($runNames.Count) run(s); $ignored row(s) for other clips ignored; rep pairs that differ: $differ of $($texts.Count)"
foreach ($f in $serverFailed.GetEnumerator()) { "SERVER FAILED for $($f.Key): $($f.Value)" }
foreach ($p in @($scored | Where-Object { $_.Rep -eq 1 } | Group-Object Run, Bias)) {
  if ($p.Count -ne $clips.Count) { "note: $($p.Name) has $($p.Count) of $($clips.Count) clips" }
}

if ($Out) {
  $scored | ForEach-Object {
    [pscustomobject]@{ Run = $_.Run; Bias = $_.Bias; Clip = $_.Clip; Set = $_.Set; Rep = $_.Rep; Ms = $_.Ms; Bad = $_.Bad; Warned = $_.Warned; Words = $_.N
                       Sub = $_.S; Del = $_.D; Ins = $_.I; Errors = $_.Err; Perfect = $_.Perfect
                       TermChances = $_.Chances.Count; TermHits = @($_.Chances | Where-Object Hit).Count }
  } | Export-Csv -LiteralPath $Out -NoTypeInformation -Encoding utf8
  "wrote $($scored.Count) rows to $Out"
}

# ---- summary -------------------------------------------------------------------------------------------------
function Cell([int]$hit, [int]$of) { if ($of -eq 0) { '-' } else { "$hit/$of" } }
$anyWarn = @($scored | Where-Object Warned).Count -gt 0   # only then is there a "warn" column
$anyGhost = $ghostRows.Count -gt 0                        # only then is there a "ghost" column
# The conditions present, in a fixed order. "shared" is ElevenLabs sent only the shared list, without its own keyterms.
$biasList = @('BIAS', 'shared', 'none') | Where-Object { $b = $_; @($scored | Where-Object { $_.Bias -eq $b }).Count -gt 0 }
# any other label is a keyterm list tried with corpus-cloud --terms (custom-<name>)
$biasList = @($biasList) + @($scored | ForEach-Object { $_.Bias } | Select-Object -Unique | Where-Object { $_ -notin 'BIAS', 'shared', 'none' } | Sort-Object)
foreach ($bias in $biasList) {
  $table = foreach ($runName in $runNames) {
    $all = @($scored | Where-Object { $_.Run -eq $runName -and $_.Bias -eq $bias })
    if (-not $all) { continue }
    $r1 = @($all | Where-Object { $_.Rep -eq 1 })
    if (-not $r1) { $r1 = $all }
    $words = ($r1 | Measure-Object N -Sum).Sum
    $errs  = ($r1 | Measure-Object Err -Sum).Sum
    $lc = @($r1 | ForEach-Object { $_.Chances } | Where-Object Listed)
    $sc2 = @($r1 | ForEach-Object { $_.Chances } | Where-Object { $_.Scribe })
    $oc = @($r1 | ForEach-Object { $_.Chances } | Where-Object { -not $_.Listed -and (-not $SplitScribe -or -not $_.Scribe) })
    $nPerfect = @($r1 | Where-Object Perfect).Count
    $lHit = @($lc | Where-Object Hit).Count
    $sHit = @($sc2 | Where-Object Hit).Count
    $oHit = @($oc | Where-Object Hit).Count
    $ghostCell = ''
    if ($anyGhost) {
      $gr = @($ghostRows | Where-Object { $_.Run -eq $runName -and $_.Bias -eq $bias -and $_.Rep -eq 1 })
      $ghostCell = if ($gr) { Cell @($gr | Where-Object Ghost).Count $gr.Count } else { '-' }
    }
    $six = @($sixRows | Where-Object { $_.Run -eq $runName -and $_.Bias -eq $bias })
    $sixCell = '-'
    if ($six) {
      $ok = 0; $tot = 0
      foreach ($g in ($six | Group-Object Clip)) { $tot++; if (@($g.Group | Where-Object { -not (SixOk $_) }).Count -eq 0) { $ok++ } }
      $sixCell = "$ok/$tot"
    }
    $line = [ordered]@{
      run      = $runName
      'WER %'  = '{0:N1}' -f (100 * $errs / [Math]::Max(1, $words))
      errors   = "$errs/$words"
      perfect  = Cell $nPerfect $r1.Count
      listed   = Cell $lHit $lc.Count
    }
    if ($SplitScribe) { $line['scribe'] = Cell $sHit $sc2.Count }
    $line['other'] = Cell $oHit $oc.Count
    $line['bad']   = @($r1 | Where-Object Bad).Count
    if ($anyWarn) { $line['warn'] = @($r1 | Where-Object Warned).Count }
    if ($anyGhost) { $line['ghost'] = $ghostCell }
    $line['6 clips'] = $sixCell
    $line['ms']      = [int](Median ($all | ForEach-Object Ms))
    $line['_wer']    = $errs / [Math]::Max(1, $words)
    [pscustomobject]$line
  }
  if ($table) {
    $title = switch ($bias) { 'BIAS' { 'WITH the shared list (what WhisperInk sends a local model; ElevenLabs: its real request, the shared list plus its own keyterms)' }
                              'shared' { 'the SHARED list only (ElevenLabs without its own keyterms)' } 'none' { 'NO list' } default { "a custom keyterm list ($bias)" } }
    "`n=== $title, best first"
    ($table | Sort-Object _wer | Select-Object -Property * -ExcludeProperty _wer | Format-Table -AutoSize | Out-String -Width 200).TrimEnd()
  }
}

# ---- details -------------------------------------------------------------------------------------------------
if ($Misses) {
  "`n=== missed focus terms (what the model wrote in their place)"
  foreach ($runName in $runNames) {
    foreach ($bias in $biasList) {
      $r1 = @($scored | Where-Object { $_.Run -eq $runName -and $_.Bias -eq $bias -and $_.Rep -eq 1 })
      $lines = foreach ($row in $r1) { foreach ($ch in $row.Chances) { if (-not $ch.Hit) { "    {0,-24} {1,-16} -> {2}{3}" -f $row.Clip, $ch.Term, $ch.Heard, $(if ($ch.Listed) { '   [on the list]' } elseif ($ch.Scribe) { '   [Scribe list]' } else { '' }) } } }
      if ($lines) { "  $runName [$bias]"; $lines }
    }
  }
}
if ($Diffs) {
  "`n=== every word error: reference>written, reference>- for a dropped word, +word for an extra one"
  foreach ($runName in $runNames) {
    foreach ($bias in $biasList) {
      $r1 = @($scored | Where-Object { $_.Run -eq $runName -and $_.Bias -eq $bias -and $_.Rep -eq 1 -and -not $_.Perfect })
      if (-not $r1) { continue }
      "  $runName [$bias]"
      foreach ($row in $r1) {
        $bits = foreach ($o in $row.Ops) { switch ($o.T) { 'sub' { "$($o.W)>$($o.H)" } 'del' { "$($o.W)>-" } 'ins' { "+$($o.H)" } } }
        "    {0,-24} {1}" -f $row.Clip, ($bits -join '  ')
      }
    }
  }
}
if ($Grid) {
  $legend = for ($k = 0; $k -lt $runNames.Count; $k++) { "$($k + 1)=$($runNames[$k])" }
  foreach ($bias in $biasList) {
    "`n=== focus terms x runs [$bias]  (ok = every occurrence right, x = none, k/n = some)   $($legend -join '  ')"
    "{0,-24} {1,-16} {2}" -f 'clip', 'term', ((1..$runNames.Count | ForEach-Object { "$_".PadRight(4) }) -join '')
    foreach ($name in ($clips.Keys | Sort-Object)) {
      foreach ($t in $clips[$name].Terms) {
        if ($t.Starts.Count -eq 0) { continue }
        $cells = foreach ($runName in $runNames) {
          $row = @($scored | Where-Object { $_.Run -eq $runName -and $_.Bias -eq $bias -and $_.Clip -eq $name -and $_.Rep -eq 1 })[0]
          if (-not $row) { '-' } else {
            $mine = @($row.Chances | Where-Object { $_.Term -eq $t.Text })
            $h = @($mine | Where-Object Hit).Count
            if ($h -eq $mine.Count) { 'ok' } elseif ($h -eq 0) { 'x' } else { "$h/$($mine.Count)" }
          }
        }
        "{0,-24} {1,-16} {2}" -f $name, $t.Text, (($cells | ForEach-Object { "$_".PadRight(4) }) -join '')
      }
    }
  }
}

if ($BySet) {
  $setNames = @($scored | ForEach-Object { $_.Set } | Select-Object -Unique | Sort-Object)
  foreach ($bias in $biasList) {
    "`n=== WER % by set [$bias]   (sets: $($setNames -join ' '))"
    $tbl = foreach ($runName in $runNames) {
      $r1 = @($scored | Where-Object { $_.Run -eq $runName -and $_.Bias -eq $bias -and $_.Rep -eq 1 })
      if (-not $r1) { continue }
      $o = [ordered]@{ run = $runName }
      foreach ($s in $setNames) {
        $g = @($r1 | Where-Object { $_.Set -eq $s })
        $o[$s] = if ($g) { '{0:N1}' -f (100 * ($g | Measure-Object Err -Sum).Sum / [Math]::Max(1, ($g | Measure-Object N -Sum).Sum)) } else { '-' }
      }
      $o['all'] = '{0:N1}' -f (100 * ($r1 | Measure-Object Err -Sum).Sum / [Math]::Max(1, ($r1 | Measure-Object N -Sum).Sum))
      [pscustomobject]$o
    }
    ($tbl | Format-Table -AutoSize | Out-String -Width 220).TrimEnd()
    "`n=== focus terms right / chances by set [$bias]"
    $tbl = foreach ($runName in $runNames) {
      $r1 = @($scored | Where-Object { $_.Run -eq $runName -and $_.Bias -eq $bias -and $_.Rep -eq 1 })
      if (-not $r1) { continue }
      $o = [ordered]@{ run = $runName }
      foreach ($s in $setNames) {
        $ch = @($r1 | Where-Object { $_.Set -eq $s } | ForEach-Object { $_.Chances })
        $o[$s] = if ($ch) { Cell @($ch | Where-Object Hit).Count $ch.Count } else { '-' }
      }
      [pscustomobject]$o
    }
    ($tbl | Format-Table -AutoSize | Out-String -Width 220).TrimEnd()
  }
}
