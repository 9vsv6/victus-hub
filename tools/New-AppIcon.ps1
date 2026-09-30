param([string]$OutIco, [string]$PreviewDir)
Add-Type -AssemblyName PresentationCore, WindowsBase
$ErrorActionPreference = 'Stop'

function Brush($hex) { $b = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.ColorConverter]::ConvertFromString($hex)); $b.Freeze(); $b }
$cream = Brush '#E8DEC7'; $dark = Brush '#1E1C1A'; $green = Brush '#86B08A'

# The I8-B design on a 64-unit grid: cream tile, dark laptop, green V on the screen.
# Small sizes get a heavier V and a slightly bigger screen so they stay readable in the tray.
function Render([int]$size) {
    $s = $size / 64.0
    $small = $size -le 24
    $dv = New-Object System.Windows.Media.DrawingVisual
    $dc = $dv.RenderOpen()
    $dc.PushTransform((New-Object System.Windows.Media.ScaleTransform $s, $s))
    $dc.DrawRoundedRectangle($cream, $null, (New-Object System.Windows.Rect 0, 0, 64, 64), 14, 14)
    if ($small) { $screen = New-Object System.Windows.Rect 8, 11, 48, 33 } else { $screen = New-Object System.Windows.Rect 10, 14, 44, 29 }
    $dc.DrawRoundedRectangle($dark, $null, $screen, 4, 4)
    $base = [System.Windows.Media.Geometry]::Parse($(if ($small) { 'M4,47 H60 L56,53 H8 Z' } else { 'M6,47 H58 L54,52 H10 Z' }))
    $dc.DrawGeometry($dark, $null, $base)
    $pen = New-Object System.Windows.Media.Pen $green, $(if ($small) { 7.5 } else { 5 })
    $pen.StartLineCap = 'Round'; $pen.EndLineCap = 'Round'; $pen.LineJoin = 'Round'
    $v = [System.Windows.Media.Geometry]::Parse($(if ($small) { 'M21,19 L32,37 L43,19' } else { 'M23,21 L32,37 L41,21' }))
    $dc.DrawGeometry($null, $pen, $v)
    $dc.Pop(); $dc.Close()
    $bmp = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $size, $size, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($dv)
    $bmp
}

function PngBytes($bmp) {
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $ms = New-Object System.IO.MemoryStream; $enc.Save($ms); $ms.ToArray()
}

# Classic 32-bit DIB entry (BGRA bottom-up + AND mask): what every Windows icon reader handles.
function DibBytes($bmp) {
    $n = $bmp.PixelWidth
    $conv = New-Object System.Windows.Media.Imaging.FormatConvertedBitmap $bmp, ([System.Windows.Media.PixelFormats]::Bgra32), $null, 0
    $px = New-Object byte[] ($n * $n * 4); $conv.CopyPixels($px, $n * 4, 0)
    $ms = New-Object System.IO.MemoryStream; $w = New-Object System.IO.BinaryWriter $ms
    $w.Write([int]40); $w.Write([int]$n); $w.Write([int]($n * 2)); $w.Write([int16]1); $w.Write([int16]32)
    $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0)
    for ($y = $n - 1; $y -ge 0; $y--) { $w.Write($px, $y * $n * 4, $n * 4) }
    $maskRow = [int]([math]::Ceiling($n / 32.0) * 4)
    $w.Write((New-Object byte[] ($maskRow * $n)))
    $w.Flush(); $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$images = @()
foreach ($sz in $sizes) {
    $bmp = Render $sz
    if ($PreviewDir) { [IO.File]::WriteAllBytes((Join-Path $PreviewDir "icon-$sz.png"), (PngBytes $bmp)) }
    $images += , @($sz, $(if ($sz -eq 256) { PngBytes $bmp } else { DibBytes $bmp }))
}

$ms = New-Object System.IO.MemoryStream; $w = New-Object System.IO.BinaryWriter $ms
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($img in $images) {
    $sz = $img[0]; $data = $img[1]
    $w.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz }))); $w.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
    $w.Write([byte]0); $w.Write([byte]0); $w.Write([int16]1); $w.Write([int16]32)
    $w.Write([int]$data.Length); $w.Write([int]$offset); $offset += $data.Length
}
foreach ($img in $images) { $w.Write([byte[]]$img[1]) }
$w.Flush(); [IO.File]::WriteAllBytes($OutIco, $ms.ToArray())
"wrote $OutIco ($($ms.Length) bytes, sizes $($sizes -join ', '))"
