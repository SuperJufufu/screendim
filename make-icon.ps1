# Generates power.ico: a rounded dark tile with a glowing green power symbol.
# Multi-size (16..256) using PNG-compressed ICO entries (Vista+ format).
# ASCII only on purpose - avoids the UTF-8/ANSI BOM pitfall in .ps1 files.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$out  = Join-Path $here 'power.ico'

function New-RoundedRect([System.Drawing.RectangleF]$r, [float]$rad) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $rad * 2
    $p.AddArc($r.X, $r.Y, $d, $d, 180, 90)
    $p.AddArc(($r.Right - $d), $r.Y, $d, $d, 270, 90)
    $p.AddArc(($r.Right - $d), ($r.Bottom - $d), $d, $d, 0, 90)
    $p.AddArc($r.X, ($r.Bottom - $d), $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-PowerBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([System.Drawing.Color]::Transparent)

    # --- background tile: rounded square, dark slate gradient ---
    $pad = [float]([math]::Max(1, [int]($size * 0.05)))
    $side = [float]($size - 2 * $pad)
    $rect = New-Object System.Drawing.RectangleF($pad, $pad, $side, $side)
    $radius = [float]($side * 0.23)
    $tile = New-RoundedRect $rect $radius

    $c1 = [System.Drawing.Color]::FromArgb(255, 32, 43, 58)    # top
    $c2 = [System.Drawing.Color]::FromArgb(255, 14, 18, 24)    # bottom
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
            (New-Object System.Drawing.PointF($pad, $pad)),
            (New-Object System.Drawing.PointF($pad, ($pad + $side))), $c1, $c2)
    $g.FillPath($bg, $tile)

    # thin green rim so it reads well on any wallpaper
    $rimPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(150, 46, 160, 67), [float]([math]::Max(1, $side * 0.035)))
    $g.DrawPath($rimPen, $tile)

    # --- power symbol: ring with a gap at the top + vertical stroke ---
    $cx = [float]($size / 2.0)
    $cy = [float]($size * 0.545)
    $r  = [float]($size * 0.275)

    $green     = [System.Drawing.Color]::FromArgb(255, 63, 185, 80)
    $greenSoft  = [System.Drawing.Color]::FromArgb(90, 63, 185, 80)
    $stroke = [float]([math]::Max(1.2, $r * 0.34))

    $ringRect = New-Object System.Drawing.RectangleF(($cx - $r), ($cy - $r), (2 * $r), (2 * $r))

    # soft glow underneath (bigger, translucent)
    $glowPen = New-Object System.Drawing.Pen($greenSoft, ($stroke * 2.1))
    $glowPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $glowPen.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawArc($glowPen, $ringRect, 300, 300)

    $pen = New-Object System.Drawing.Pen($green, $stroke)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
    # 300 -> 600 (== 240) leaves a 60 degree gap centred on 12 o'clock
    $g.DrawArc($pen, $ringRect, 300, 300)

    # vertical stroke through the gap, ending near the centre
    $g.DrawLine($pen, $cx, [float]($cy - $r * 1.18), $cx, [float]($cy - $r * 0.12))

    $pen.Dispose(); $glowPen.Dispose(); $rimPen.Dispose(); $bg.Dispose(); $tile.Dispose(); $g.Dispose()
    return $bmp
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$pngs  = @()
foreach ($s in $sizes) {
    $bmp = New-PowerBitmap $s
    $ms  = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += ,$ms.ToArray()
    $ms.Dispose(); $bmp.Dispose()
}

# ---- write the ICO container ----
$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([UInt16]0)                  # reserved
$bw.Write([UInt16]1)                  # type = icon
$bw.Write([UInt16]$sizes.Count)       # image count

$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $b = if ($s -ge 256) { [byte]0 } else { [byte]$s }
    $bw.Write([byte]$b)               # width  (0 == 256)
    $bw.Write([byte]$b)               # height
    $bw.Write([byte]0)                # palette
    $bw.Write([byte]0)                # reserved
    $bw.Write([UInt16]1)              # colour planes
    $bw.Write([UInt16]32)             # bits per pixel
    $bw.Write([UInt32]$pngs[$i].Length)
    $bw.Write([UInt32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Flush(); $bw.Close(); $fs.Close()

Write-Host "wrote $out  ($([math]::Round((Get-Item $out).Length / 1KB, 1)) KB, $($sizes.Count) sizes: $($sizes -join ','))"

# also drop a 256px preview for documentation
$preview = New-PowerBitmap 256
$preview.Save((Join-Path $here 'docs-icon.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$preview.Dispose()
Write-Host "wrote docs-icon.png (256px preview)"
