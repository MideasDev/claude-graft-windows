# Draws src/ClaudeGraft/app.ico: the program's own icon, a rounded orange
# square with two offset squares on it (one Claude grafted onto another).
# Run it again only to change the drawing; the .ico is checked in.
param([string]$Out = (Join-Path $PSScriptRoot '..\src\ClaudeGraft\app.ico'))

Add-Type -AssemblyName System.Drawing

function Rounded([System.Drawing.RectangleF]$r, [float]$radius) {
    $d = $radius * 2
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddArc($r.X, $r.Y, $d, $d, 180, 90)
    $p.AddArc($r.Right - $d, $r.Y, $d, $d, 270, 90)
    $p.AddArc($r.Right - $d, $r.Bottom - $d, $d, $d, 0, 90)
    $p.AddArc($r.X, $r.Bottom - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    $p
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$frames = foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $bg = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0xC2, 0x41, 0x0C))
    $g.FillPath($bg, (Rounded (New-Object System.Drawing.RectangleF 0, 0, $s, $s) ($s * 0.22)))
    $back = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(150, 255, 255, 255))
    $front = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $g.FillPath($back, (Rounded (New-Object System.Drawing.RectangleF ($s * 0.18), ($s * 0.18), ($s * 0.42), ($s * 0.42)) ($s * 0.08)))
    $g.FillPath($front, (Rounded (New-Object System.Drawing.RectangleF ($s * 0.40), ($s * 0.40), ($s * 0.42), ($s * 0.42)) ($s * 0.08)))
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    , $ms.ToArray()
}

$file = [System.IO.File]::Create($Out)
$w = New-Object System.IO.BinaryWriter $file
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $b = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$b); $w.Write([byte]$b); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([int16]1); $w.Write([int16]32)
    $w.Write([int32]$frames[$i].Length); $w.Write([int32]$offset)
    $offset += $frames[$i].Length
}
foreach ($f in $frames) { $w.Write($f) }
$w.Close()
Write-Host "wrote $Out"
