<#
  get-model.ps1 - download a CrispASR model (.gguf) from Hugging Face into
  WhisperInk's model folder.

  WhisperInk then lists it under Provider > "New in the model folder", and one
  click adds it as a local provider. Nothing to edit, nothing to rebuild.

  USAGE
    scripts\get-model.ps1 cstr/orukeet-GGUF                      # list the repo's .gguf files
    scripts\get-model.ps1 cstr/orukeet-GGUF orukeet-q4_k.gguf    # download one
    scripts\get-model.ps1 https://huggingface.co/cstr/orukeet-GGUF/blob/main/orukeet-q4_k.gguf

  WHY NOT JUST CURL IT
    - It downloads to <file>.part and renames it only once its size and
      SHA-256 match what Hugging Face publishes, so WhisperInk never sees a
      half-written or corrupt model.
    - A download that stops part way resumes when you run it again.
    - It never overwrites a model that is already there.

  Pure ASCII on purpose: Windows PowerShell 5.1 reads a BOM-less script as ANSI.
#>
param(
    [Parameter(Mandatory = $true, Position = 0)][string]$Repo,
    [Parameter(Position = 1)][string]$File,
    [string]$Revision = 'main',
    [string]$Folder = (Join-Path $env:APPDATA '.WhisperInk\cohere-gguf')
)
$ErrorActionPreference = 'Stop'

# A pasted link: https://huggingface.co/<owner>/<repo>[/blob|resolve/<revision>/<file>]
if ($Repo -match '^https?://huggingface\.co/([^/]+/[^/]+)/(?:blob|resolve)/([^/]+)/([^?#]+)') {
    $Repo = $Matches[1]; $Revision = $Matches[2]; $File = [uri]::UnescapeDataString($Matches[3])
} elseif ($Repo -match '^https?://huggingface\.co/([^/]+/[^/?#]+)') {
    $Repo = $Matches[1]
}

try {
    $tree = Invoke-RestMethod "https://huggingface.co/api/models/$Repo/tree/$Revision" -TimeoutSec 30
} catch {
    throw "Couldn't list $Repo ($Revision) on Hugging Face: $($_.Exception.Message). A gated or private repo needs you signed in to download it in a browser."
}
$ggufs = @($tree | Where-Object { $_.type -eq 'file' -and $_.path -like '*.gguf' })

function SizeOf($entry) { if ($entry.lfs) { [int64]$entry.lfs.size } else { [int64]$entry.size } }

if (-not $File) {
    if ($ggufs.Count -eq 0) { "No .gguf files in $Repo ($Revision)."; return }
    "GGUF files in $Repo ($Revision):"
    foreach ($g in $ggufs) { "  {0,8:N0} MB  {1}" -f ((SizeOf $g) / 1MB), $g.path }
    ""
    "Download one with:  scripts\get-model.ps1 $Repo <file>"
    return
}

$entry = $ggufs | Where-Object { $_.path -eq $File } | Select-Object -First 1
if (-not $entry) { throw "$File is not a .gguf in $Repo ($Revision). Run it without a file name to list them." }
$size = SizeOf $entry
$sha = if ($entry.lfs) { "$($entry.lfs.oid)".ToLowerInvariant() } else { $null }

$name = Split-Path $File -Leaf
$dst = Join-Path $Folder $name
if (Test-Path $dst) { "Already there: $dst"; return }
New-Item -ItemType Directory -Force -Path $Folder | Out-Null
$part = "$dst.part"

$have = if (Test-Path $part) { (Get-Item $part).Length } else { 0 }
if ($have -lt $size) {
    $url = "https://huggingface.co/$Repo/resolve/$Revision/$File"
    if ($have -gt 0) { "Resuming $name at {0:N0} of {1:N0} MB ..." -f ($have / 1MB), ($size / 1MB) }
    else { "Downloading $name ({0:N0} MB) from $Repo ..." -f ($size / 1MB) }
    & curl.exe -L --fail --retry 3 -C - -o $part $url
    if ($LASTEXITCODE -ne 0) { throw "Download failed (curl exit $LASTEXITCODE). Run the same command again to resume." }
}

$got = (Get-Item $part).Length
if ($got -ne $size) { throw "Size mismatch: $got bytes, expected $size. Run again to resume, or delete $part to start over." }
if ($sha) {
    "Checking SHA-256 ..."
    $hash = (Get-FileHash $part -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $sha) {
        Remove-Item $part
        throw "SHA-256 mismatch (got $hash, expected $sha). The download was deleted; run again to fetch it afresh."
    }
}
Move-Item $part $dst
"Done: $dst"
"In WhisperInk: right-click the bar or the tray icon > Provider > the line starting with + for this model."
