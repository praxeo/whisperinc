#requires -Version 7
<#
  _ab_compare.ps1 - compare two _local_bias_ab.ps1 -Csv files made from the
  same inputs: Old (normally the deployed crispasr.exe) and New (a release
  being A/B'd before it's deployed), and say whether New is safe to deploy.

  Each transcript pair is the same, differs only in case or punctuation
  ("cosmetic"), or differs in its words. A transcript is BAD when it's empty,
  an <error: ...>, holds <unk>, or recites the bias list (LIST?). New fails
  the gate on any of:
    - an input Old transcribed that New has no row for (its server failed);
    - a regression: Old good, New bad;
    - different words, unless the run matches -Expect (a model the release is
      meant to change), the input matches -ReportOnly, or it's an improvement
      (Old bad, New good);
    - a median time more than 25 % and 50 ms slower than Old's.
  Everything else is reported. -Score adds the clinical clips' own terms per
  run, binary and condition (the sentences are in RECORD_THESE.md).
  Exit code 0 = GATE: PASS, 1 = GATE: FAIL.

  USAGE  pwsh .\_ab_compare.ps1 -Old old.csv -New new.csv
         pwsh .\_ab_compare.ps1 -Old old.csv -New new.csv -Expect 'voxtral mini 3b',granite -ReportOnly tail,straddle,long_ -Score
#>
param(
  [Parameter(Mandatory)][string]$Old,
  [Parameter(Mandatory)][string]$New,
  [string[]]$Expect = @(),
  [string[]]$ReportOnly = @(),
  [switch]$Score
)
$ErrorActionPreference = 'Stop'
# `pwsh -File` hands "a,b" over as one string, so these are split here too.
$Expect     = @($Expect | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$ReportOnly = @($ReportOnly | ForEach-Object { $_ -split ',' } | Where-Object { $_ })

function Norm([string]$s) { (($s.ToLowerInvariant() -replace '[^\p{L}\p{N}\s]', ' ') -replace '\s+', ' ').Trim() }
function IsBad($r) { $r.Text -eq '' -or $r.Text -like '<error*' -or $r.Text -match '<unk>' -or $r.Recited -eq 'True' }
function Like([string]$s, [string[]]$pats) { @($pats | Where-Object { $s -like "*$_*" }).Count -gt 0 }
function Key($r) { "$($r.Run)|$($r.Clip)|$($r.Bias)|$($r.Rep)" }
function Median($v) {
  $s = @($v | Sort-Object); $c = $s.Count
  if ($c -eq 0) { return [double]::NaN }
  if ($c % 2) { return $s[($c - 1) / 2] }
  return ($s[$c / 2 - 1] + $s[$c / 2]) / 2
}

$o = @(Import-Csv -LiteralPath $Old)
$n = @(Import-Csv -LiteralPath $New)
$newBy = @{}; foreach ($r in $n) { $newBy[(Key $r)] = $r }
$oldRuns = @($o | ForEach-Object Run | Select-Object -Unique)
$fails = [System.Collections.Generic.List[string]]::new()

foreach ($run in $oldRuns) {
  $oldRows = @($o | Where-Object Run -eq $run)
  if ($oldRows | Where-Object Clip -eq '(server)') { "`n=== $($run): the OLD binary couldn't run it, so it isn't compared"; continue }
  $expected = Like $run $Expect
  $st = [ordered]@{ same = 0; cosmetic = 0; words = 0; improved = 0; regressed = 0; missing = 0 }
  $gatedWords = 0
  $detail = [System.Collections.Generic.List[string]]::new()
  $oldMs = [System.Collections.Generic.List[double]]::new()
  $newMs = [System.Collections.Generic.List[double]]::new()
  foreach ($a in $oldRows) {
    $b = $newBy[(Key $a)]
    $where = "$($a.Clip) $($a.Bias) rep$($a.Rep)"
    if (-not $b) { $st.missing++; $detail.Add("  MISSING    $where"); continue }
    $aBad = IsBad $a; $bBad = IsBad $b
    if (-not $aBad -and -not $bBad) { $oldMs.Add([double]$a.Ms); $newMs.Add([double]$b.Ms) }
    $pair = "`n      old: $($a.Text)`n      new: $($b.Text)"
    if (-not $aBad -and $bBad) { $st.regressed++; $detail.Add("  REGRESSED  $where$pair"); continue }
    if ($aBad -and -not $bBad) { $st.improved++;  $detail.Add("  improved   $where$pair"); continue }
    if ($a.Text -ceq $b.Text) { $st.same++; continue }
    if ((Norm $a.Text) -eq (Norm $b.Text)) { $st.cosmetic++; $detail.Add("  cosmetic   $where$pair"); continue }
    $st.words++
    $gated = -not $expected -and -not (Like $a.Clip $ReportOnly)
    if ($gated) { $gatedWords++ }
    $detail.Add("  $(if ($gated) { 'WORDS     ' } else { 'words     ' }) $where$pair")
  }
  $mo = Median $oldMs; $mn = Median $newMs
  $pct = if ($mo -gt 0) { [int](100 * ($mn - $mo) / $mo) } else { 0 }
  $counts = ($st.GetEnumerator() | ForEach-Object { "$($_.Value) $($_.Key)" }) -join ', '
  "`n=== $($run)$(if ($expected) { ' (expected to change)' }): $counts; median $([int]$mo) -> $([int]$mn) ms ($('{0:+0;-0;0}' -f $pct) %)"
  $detail
  if ($st.missing)   { $fails.Add("$($run): $($st.missing) input(s) missing from New (did its server fail?)") }
  if ($st.regressed) { $fails.Add("$($run): $($st.regressed) regression(s): good before, empty/error/<unk>/list now") }
  if ($gatedWords)   { $fails.Add("$($run): $gatedWords transcript(s) with different words") }
  if ($mn -gt $mo * 1.25 -and ($mn - $mo) -gt 50) { $fails.Add("$($run): median $([int]$mo) -> $([int]$mn) ms") }
}
foreach ($run in @($n | ForEach-Object Run | Select-Object -Unique | Where-Object { $_ -notin $oldRuns })) { "`n=== $($run): only in New, not compared" }

if ($Score) {
  $targets = [ordered]@{ hematochezia_1 = 'hematochezia'; hematochezia_2 = 'hematochezia'; ureterolithiasis = 'ureterolithiasis'
                         biliary_colic = 'biliary colic'; ureteral_colic = 'ureteral colic'; neutral = 'stable condition' }
  $cfg = Get-Content (Join-Path $env:APPDATA '.WhisperInk\config.json') -Raw | ConvertFrom-Json
  $terms = @($cfg.ContextBiasTerms | Where-Object { $_ })
  function Has([string]$text, [string]$phrase) { $text -match ('(?i)(?<!\w)' + [regex]::Escape($phrase) + '(?!\w)') }
  function ClipOk($row, [string]$clip) {
    if (-not (Has $row.Text $targets[$clip])) { return $false }
    if ($clip -ne 'neutral') { return $true }
    foreach ($t in $terms) { if (Has $row.Text $t) { return $false } }
    return $true
  }
  "`nClinical clips: ok = the clip's own term in every rep (neutral: its sentence and no list term), NO = in none, k/n = in some"
  "{0,-22} {1,-4} {2,-5} {3}" -f 'run', 'bin', 'list', (($targets.Keys | ForEach-Object { $_.PadRight(17) }) -join '')
  foreach ($run in @(@($o) + @($n) | ForEach-Object Run | Select-Object -Unique)) {
    foreach ($bin in 'old', 'new') {
      $set = if ($bin -eq 'old') { $o } else { $n }
      foreach ($mode in 'none', 'BIAS') {
        $cells = foreach ($clip in $targets.Keys) {
          $rs = @($set | Where-Object { $_.Run -eq $run -and $_.Clip -eq $clip -and $_.Bias -eq $mode })
          if (-not $rs) { '-' } else {
            $hits = @($rs | Where-Object { ClipOk $_ $clip }).Count
            if ($hits -eq $rs.Count) { 'ok' } elseif ($hits -eq 0) { 'NO' } else { "$hits/$($rs.Count)" }
          }
        }
        "{0,-22} {1,-4} {2,-5} {3}" -f $run, $bin, $mode, (($cells | ForEach-Object { "$_".PadRight(17) }) -join '')
      }
    }
  }
}

if ($fails.Count) { "`nGATE: FAIL"; $fails | ForEach-Object { "  - $_" }; exit 1 }
"`nGATE: PASS"
exit 0
