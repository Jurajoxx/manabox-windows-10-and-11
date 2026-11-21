# Regenerates assets\app.ico (only needed if you want to change the icon).
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-RoundRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-IconPng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.ScaleTransform($size / 256.0, $size / 256.0)

    # Background tile
    $bg = New-RoundRect 8 8 240 240 54
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush (
        (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF 256, 256),
        [System.Drawing.Color]::FromArgb(255, 146, 116, 255), [System.Drawing.Color]::FromArgb(255, 78, 48, 214))
    $g.FillPath($grad, $bg)

    # Back card
    $g.TranslateTransform(128, 132); $g.RotateTransform(-14); $g.TranslateTransform(-128, -132)
    $back = New-RoundRect 70 52 104 146 14
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(120, 255, 255, 255))), $back)
    $g.ResetTransform(); $g.ScaleTransform($size / 256.0, $size / 256.0)

    # Front card
    $g.TranslateTransform(128, 132); $g.RotateTransform(8); $g.TranslateTransform(-128, -132)
    $front = New-RoundRect 84 54 108 150 14
    $g.FillPath([System.Drawing.Brushes]::White, $front)
    # Gem
    $gem = New-Object System.Drawing.Drawing2D.GraphicsPath
    $gem.AddPolygon(@(
        (New-Object System.Drawing.PointF 138, 92), (New-Object System.Drawing.PointF 166, 128),
        (New-Object System.Drawing.PointF 138, 166), (New-Object System.Drawing.PointF 110, 128)))
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 108, 78, 240))), $gem)
    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return ,$ms.ToArray()
}

$sizes = 256, 64, 48, 32, 24, 16
$images = $sizes | ForEach-Object { ,(New-IconPng $_) }

$out = Join-Path (Split-Path $PSScriptRoot -Parent) 'assets\app.ico'
New-Item -ItemType Directory -Force (Split-Path $out -Parent) | Out-Null
$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$images[$i].Length)
    $bw.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $bw.Write([byte[]]$img) }
$bw.Dispose(); $fs.Dispose()
Write-Host "Wrote $out"
