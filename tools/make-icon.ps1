# Draws the Glass Mixer icon (teal rounded square, white speaker) at every size Windows
# uses and packs them into one .ico file. Run it again after changing the design:
#   powershell -ExecutionPolicy Bypass -File tools/make-icon.ps1
param([string]$Out = "$PSScriptRoot\..\src\GlassMixer.App\app.ico")

Add-Type -AssemblyName System.Drawing
$sizes = 16, 20, 24, 32, 40, 48, 64, 256

function Draw-Icon([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'

    # Rounded square with the mockup's teal, lighter at the top left.
    $r = [Math]::Max(3, $s * 0.22)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, 2 * $r, 2 * $r, 180, 90)
    $path.AddArc($s - 2 * $r - 1, 0, 2 * $r, 2 * $r, 270, 90)
    $path.AddArc($s - 2 * $r - 1, $s - 2 * $r - 1, 2 * $r, 2 * $r, 0, 90)
    $path.AddArc(0, $s - 2 * $r - 1, 2 * $r, 2 * $r, 90, 90)
    $path.CloseFigure()
    $rect = New-Object System.Drawing.RectangleF 0, 0, $s, $s
    $fill = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect,
        ([System.Drawing.ColorTranslator]::FromHtml('#45cfc1')), ([System.Drawing.ColorTranslator]::FromHtml('#0b6f67')), 45
    $g.FillPath($fill, $path)

    # The speaker glyph from the UI (24-unit grid), scaled into the square.
    $k = $s / 24 * 0.78
    $o = $s * 0.08
    function P([double]$x, [double]$y) { New-Object System.Drawing.PointF ($o + $x * $k), ($o + $y * $k) }
    $white = [System.Drawing.Brushes]::White
    $g.FillPolygon($white, [System.Drawing.PointF[]]@((P 3.5 9), (P 7.5 9), (P 12.5 4.8), (P 12.5 19.2), (P 7.5 15), (P 3.5 15)))

    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([Math]::Max(1.3, 2.1 * $k))
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    # Sound waves: arcs around the speaker's mouth. Small sizes get one wave so it stays readable.
    # A List keeps each (centre, radius) pair intact; PowerShell would flatten a one-pair array.
    $waves = New-Object 'System.Collections.Generic.List[double[]]'
    if ($s -ge 32) { $waves.Add(@(13.0, 3.6)); $waves.Add(@(12.9, 7.0)) } else { $waves.Add(@(13.0, 4.2)) }
    foreach ($w in $waves) {
        $cx = $w[0]; $rad = $w[1]
        $g.DrawArc($pen, [float]($o + ($cx + 2 - $rad) * $k), [float]($o + (12 - $rad) * $k),
            [float](2 * $rad * $k), [float](2 * $rad * $k), [float]-46, [float]92)
    }
    $g.Dispose()
    return $bmp
}

# .ico layout: a 6-byte header, a 16-byte entry per image, then the PNG data of each image.
$pngs = foreach ($s in $sizes) {
    $bmp = Draw-Icon $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}
$ico = New-Object System.IO.MemoryStream
$w = [System.IO.BinaryWriter]::new($ico)
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$pngs[$i].Length); $w.Write([UInt32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Flush()
[IO.File]::WriteAllBytes((Join-Path (Resolve-Path (Split-Path $Out)) (Split-Path $Out -Leaf)), $ico.ToArray())

# A large preview for checking the design.
$preview = Draw-Icon 256
$preview.Save("$env:TEMP\glassmixer-icon-preview.png")   # outside the repo
$preview.Dispose()
"Wrote $Out ($($ico.Length) bytes, sizes $($sizes -join ', '))"
