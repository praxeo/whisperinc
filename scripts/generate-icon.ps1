# ===================================================================
#  Generate WhisperInk application icon (Assets/icon.ico)
# ===================================================================
# Run once. Produces a multi-size Windows .ico from a programmatic
# GDI+ bitmap — a dark blue square with a white "W" glyph. Sizes
# embedded: 16, 32, 48, 64, 128, 256.
# ===================================================================
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Drawing

$repoRoot = Split-Path -Parent $PSScriptRoot
$outDir   = Join-Path $repoRoot "Assets"
$outFile  = Join-Path $outDir "icon.ico"

if (-not (Test-Path $outDir)) {
    New-Item -ItemType Directory -Path $outDir | Out-Null
}

$sizes = @(16, 32, 48, 64, 128, 256)
$bitmaps = @()

foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g   = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode       = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode   = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.TextRenderingHint   = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit

    $bg = [System.Drawing.Color]::FromArgb(255, 20, 80, 160)
    $g.Clear($bg)

    $fontSize = [int]([Math]::Max(8, $s * 0.62))
    $font = New-Object System.Drawing.Font("Segoe UI", $fontSize, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $brush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $sf = New-Object System.Drawing.StringFormat
    $sf.Alignment     = [System.Drawing.StringAlignment]::Center
    $sf.LineAlignment = [System.Drawing.StringAlignment]::Center

    $rect = New-Object System.Drawing.RectangleF(0, [float](-$s * 0.04), [float]$s, [float]$s)
    $g.DrawString("W", $font, $brush, $rect, $sf)

    $font.Dispose()
    $brush.Dispose()
    $g.Dispose()

    $bitmaps += ,$bmp
}

# Encode each bitmap as PNG bytes (works at all sizes, including 256).
$pngBlobs = @()
foreach ($bmp in $bitmaps) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngBlobs += ,$ms.ToArray()
    $ms.Dispose()
    $bmp.Dispose()
}

# Build ICO container: 6-byte header + 16-byte dir entry per image + PNG blobs.
$headerSize = 6
$entrySize  = 16
$dirSize    = $headerSize + ($entrySize * $pngBlobs.Count)

$out = New-Object System.IO.MemoryStream
$bw  = New-Object System.IO.BinaryWriter($out)

$bw.Write([UInt16]0)    # reserved
$bw.Write([UInt16]1)    # type = icon
$bw.Write([UInt16]$pngBlobs.Count)

$offset = $dirSize
for ($i = 0; $i -lt $pngBlobs.Count; $i++) {
    $s = $sizes[$i]
    $blob = $pngBlobs[$i]
    $w = if ($s -ge 256) { [byte]0 } else { [byte]$s }
    $h = $w
    $bw.Write([byte]$w)           # width
    $bw.Write([byte]$h)           # height
    $bw.Write([byte]0)            # color count
    $bw.Write([byte]0)            # reserved
    $bw.Write([UInt16]1)          # color planes
    $bw.Write([UInt16]32)         # bits per pixel
    $bw.Write([UInt32]$blob.Length)
    $bw.Write([UInt32]$offset)
    $offset += $blob.Length
}

foreach ($blob in $pngBlobs) {
    $bw.Write($blob)
}

$bw.Flush()
[System.IO.File]::WriteAllBytes($outFile, $out.ToArray())
$bw.Dispose()
$out.Dispose()

Write-Host "Wrote $outFile ($((Get-Item $outFile).Length) bytes, $($pngBlobs.Count) sizes)"
