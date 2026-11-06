<#
  Genera l'icona dell'app (monitor con due schede in alto e un puntatore) in un .ico a più dimensioni:
  16, 24, 32, 48, 64, 128 (bitmap 32 bit) e 256 (PNG). Uso:  pwsh assets/genera-icona.ps1
  Scrive src/RdpManager/app.ico (usata da ApplicationIcon, dalle finestre e dagli installer).
#>
param([string]$Out = (Join-Path $PSScriptRoot '..\src\RdpManager\app.ico'))

Add-Type -AssemblyName System.Drawing

function New-RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.ScaleTransform($size / 256.0, $size / 256.0)

    $dark = [System.Drawing.Color]::FromArgb(38, 44, 58)
    # corpo del monitor
    $b = New-Object System.Drawing.SolidBrush $dark
    $g.FillPath($b, (New-RoundedPath 12 20 232 176 26))
    # collo e base
    $g.FillRectangle($b, 108, 192, 40, 28)
    $g.FillPath($b, (New-RoundedPath 70 216 116 22 11))
    # schede: una dietro (scura) e una attiva (chiara)
    $tab2 = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(84, 96, 122))
    $g.FillPath($tab2, (New-RoundedPath 122 38 78 40 10))
    $tab1 = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(120, 200, 255))
    $g.FillPath($tab1, (New-RoundedPath 28 34 100 44 10))
    # schermo
    $rect = New-Object System.Drawing.RectangleF 28, 74, 200, 104
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(70, 160, 240)), ([System.Drawing.Color]::FromArgb(28, 80, 170)), 90.0
    $g.FillRectangle($grad, $rect)
    # puntatore del mouse, con contorno scuro
    $pts = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF 120, 92), (New-Object System.Drawing.PointF 120, 164),
        (New-Object System.Drawing.PointF 140, 146), (New-Object System.Drawing.PointF 156, 178),
        (New-Object System.Drawing.PointF 170, 171), (New-Object System.Drawing.PointF 154, 140),
        (New-Object System.Drawing.PointF 182, 140))
    $pen = New-Object System.Drawing.Pen $dark, 7
    $pen.LineJoin = 'Round'
    $g.DrawPolygon($pen, $pts)
    $g.FillPolygon([System.Drawing.Brushes]::White, $pts)
    $g.Dispose()
    return $bmp
}

function Get-BmpEntry([System.Drawing.Bitmap]$bmp) {
    $n = $bmp.Width
    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $ms
    $w.Write([int]40); $w.Write([int]$n); $w.Write([int]($n * 2))
    $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]0)
    $w.Write([int]($n * $n * 4)); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0)
    for ($y = $n - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $n; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $w.Write([byte]$c.B); $w.Write([byte]$c.G); $w.Write([byte]$c.R); $w.Write([byte]$c.A)
        }
    }
    $stride = [int]([math]::Ceiling($n / 32.0) * 4)
    $w.Write([byte[]](New-Object byte[] ($stride * $n)))   # maschera AND a zero: conta il canale alfa
    $w.Flush()
    return , $ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$entries = @()
foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s
    if ($s -ge 256) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $entries += , $ms.ToArray()
    } else {
        $entries += , (Get-BmpEntry $bmp)
    }
    $bmp.Dispose()
}

$fs = [System.IO.File]::Create([System.IO.Path]::GetFullPath($Out))
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([int16]0); $bw.Write([int16]1); $bw.Write([int16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([int16]1); $bw.Write([int16]32)
    $bw.Write([int]$entries[$i].Length); $bw.Write([int]$offset)
    $offset += $entries[$i].Length
}
foreach ($e in $entries) { $bw.Write([byte[]]$e) }
$bw.Dispose(); $fs.Dispose()
Write-Host "Icona scritta in $Out"
