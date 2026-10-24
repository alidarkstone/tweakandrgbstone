# Draws the rgbstone logo (a faceted RGB gemstone) and writes assets\rgbstone.ico, logo-*.png and logo-banner.png.
param([string]$Out = (Join-Path $PSScriptRoot "..\assets"))
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $Out | Out-Null

function HsvColor([double]$h, [double]$s = 1, [double]$v = 1, [int]$a = 255) {
    $h = $h - [math]::Floor($h); $i = [math]::Floor($h * 6); $f = $h * 6 - $i
    $p = $v * (1 - $s); $q = $v * (1 - $s * $f); $t = $v * (1 - $s * (1 - $f))
    switch ($i % 6) { 0 { $r=$v;$g=$t;$b=$p } 1 { $r=$q;$g=$v;$b=$p } 2 { $r=$p;$g=$v;$b=$t } 3 { $r=$p;$g=$q;$b=$v } 4 { $r=$t;$g=$p;$b=$v } 5 { $r=$v;$g=$p;$b=$q } }
    [System.Drawing.Color]::FromArgb($a, [int]($r*255), [int]($g*255), [int]($b*255))
}

function Draw-Gem([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.Clear([System.Drawing.Color]::Transparent)
    $cx = $size / 2.0; $cy = $size / 2.0; $Ro = $size * 0.47; $Ri = $Ro * 0.46

    # outer hexagon (pointy top) and inner hexagon (the "table" of the cut stone)
    $outer = 0..5 | ForEach-Object { $a = [math]::PI / 180 * (60 * $_ - 90); New-Object System.Drawing.PointF ([single]($cx + $Ro * [math]::Cos($a))), ([single]($cy + $Ro * [math]::Sin($a))) }
    $inner = 0..5 | ForEach-Object { $a = [math]::PI / 180 * (60 * $_ - 90); New-Object System.Drawing.PointF ([single]($cx + $Ri * [math]::Cos($a))), ([single]($cy - $size*0.02 + $Ri * [math]::Sin($a))) }

    # six crown facets, hue going around the stone: red at top, green lower right, blue lower left
    for ($i = 0; $i -lt 6; $i++) {
        $j = ($i + 1) % 6
        $poly = [System.Drawing.PointF[]]@($outer[$i], $outer[$j], $inner[$j], $inner[$i])
        $hue = ($i + 0.5) / 6.0
        $c1 = HsvColor $hue 0.95 1.0; $c2 = HsvColor ($hue + 0.08) 1.0 0.62
        $br = New-Object System.Drawing.Drawing2D.LinearGradientBrush $outer[$i], $inner[$j], $c1, $c2
        $g.FillPolygon($br, $poly); $br.Dispose()
    }
    # table: dark glassy center with a soft highlight
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath; $path.AddPolygon([System.Drawing.PointF[]]$inner)
    $pg = New-Object System.Drawing.Drawing2D.PathGradientBrush $path
    $pg.CenterColor = [System.Drawing.Color]::FromArgb(255, 70, 74, 92); $pg.SurroundColors = @([System.Drawing.Color]::FromArgb(255, 18, 19, 26))
    $pg.CenterPoint = New-Object System.Drawing.PointF ([single]($cx - $Ri*0.25)), ([single]($cy - $Ri*0.35))
    $g.FillPath($pg, $path); $pg.Dispose()
    # facet edges
    $w = [single][math]::Max(1, $size / 64.0)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(150, 10, 10, 16)), $w
    for ($i = 0; $i -lt 6; $i++) { $g.DrawLine($pen, $outer[$i], $inner[$i]) }
    $g.DrawPolygon($pen, [System.Drawing.PointF[]]$inner)
    $pen2 = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(230, 12, 12, 18)), ([single]($w * 1.6))
    $g.DrawPolygon($pen2, [System.Drawing.PointF[]]$outer)
    # glint
    if ($size -ge 32) {
        $gl = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(200, 255, 255, 255))
        $s = $size * 0.07; $gx = $cx - $Ri * 0.35; $gy = $cy - $Ri * 0.55
        $g.FillPolygon($gl, [System.Drawing.PointF[]]@((New-Object System.Drawing.PointF $gx, ($gy - $s)), (New-Object System.Drawing.PointF ($gx + $s*0.3), ($gy - $s*0.3)), (New-Object System.Drawing.PointF ($gx + $s), $gy), (New-Object System.Drawing.PointF ($gx + $s*0.3), ($gy + $s*0.3)), (New-Object System.Drawing.PointF $gx, ($gy + $s)), (New-Object System.Drawing.PointF ($gx - $s*0.3), ($gy + $s*0.3)), (New-Object System.Drawing.PointF ($gx - $s), $gy), (New-Object System.Drawing.PointF ($gx - $s*0.3), ($gy - $s*0.3))))
    }
    $g.Dispose()
    return $bmp
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = @{}
foreach ($s in $sizes) {
    $b = Draw-Gem $s
    $ms = New-Object System.IO.MemoryStream; $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $pngs[$s] = $ms.ToArray()
    if ($s -in 64, 256) { $b.Save((Join-Path $Out "logo-$s.png"), [System.Drawing.Imaging.ImageFormat]::Png) }
    $b.Dispose()
}

# ICO container with PNG-compressed images
$fs = [System.IO.File]::Create((Join-Path $Out "rgbstone.ico")); $bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
foreach ($s in $sizes) {
    $d = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$d); $bw.Write([byte]$d); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$pngs[$s].Length); $bw.Write([uint32]$offset)
    $offset += $pngs[$s].Length
}
foreach ($s in $sizes) { $bw.Write($pngs[$s]) }
$bw.Close()

# banner: gem + wordmark with an RGB gradient on "rgb"
$W = 900; $H = 260
$ban = New-Object System.Drawing.Bitmap $W, $H
$g = [System.Drawing.Graphics]::FromImage($ban); $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
$g.Clear([System.Drawing.Color]::FromArgb(255, 17, 18, 24))
$gem = Draw-Gem 200; $g.DrawImage($gem, 30, 30, 200, 200)
$font = New-Object System.Drawing.Font "Segoe UI Semibold", 110, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
$rgbSize = $g.MeasureString("rgb", $font)
$grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 250, 0), (New-Object System.Drawing.PointF (250 + $rgbSize.Width), 0), ([System.Drawing.Color]::FromArgb(255, 255, 60, 80)), ([System.Drawing.Color]::FromArgb(255, 60, 120, 255))
$blend = New-Object System.Drawing.Drawing2D.ColorBlend 3
$blend.Colors = @([System.Drawing.Color]::FromArgb(255, 255, 59, 92), [System.Drawing.Color]::FromArgb(255, 60, 220, 120), [System.Drawing.Color]::FromArgb(255, 70, 120, 255)); $blend.Positions = @([single]0, [single]0.5, [single]1)
$grad.InterpolationColors = $blend
$g.DrawString("rgb", $font, $grad, 245, 50)
$g.DrawString("stone", $font, (New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 236, 238, 245))), (245 + $rgbSize.Width - 38), 50)
$g.Dispose()
$ban.Save((Join-Path $Out "logo-banner.png"), [System.Drawing.Imaging.ImageFormat]::Png)
"logo written to $Out"

