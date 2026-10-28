# Generates the Miles Font Extractor brand assets from one geometric definition:
#   src\MilesFontExtractor\Assets\app.ico, docs\logo.svg, docs\social-preview.png
# The mark is a rounded square with a monogram "M" on a 100x100 grid. Keep these points in sync with
# Controls\MilesMark.xaml and docs\logo.svg.
param([string]$Root = (Split-Path -Parent $PSScriptRoot))
Add-Type -AssemblyName System.Drawing

$pts = @(@(29, 71), @(29, 31), @(50, 55), @(71, 31), @(71, 71))   # the M
$c1 = [System.Drawing.Color]::FromArgb(255, 108, 124, 255)
$c2 = [System.Drawing.Color]::FromArgb(255, 160, 108, 255)

function Draw-Mark($g, [single]$x, [single]$y, [single]$size) {
    $k = $size / 100.0
    $rect = New-Object System.Drawing.RectangleF $x, $y, $size, $size
    $r = 26 * $k; $d = $r * 2
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, $c1, $c2, 45.0
    $g.FillPath($brush, $path)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([single](9 * $k))
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
    $p = @(); foreach ($q in $pts) { $p += New-Object System.Drawing.PointF ([single]($x + $q[0] * $k)), ([single]($y + $q[1] * $k)) }
    $g.DrawLines($pen, [System.Drawing.PointF[]]$p)
}

function New-Frame([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.Clear([System.Drawing.Color]::Transparent)
    Draw-Mark $g 0 0 $s
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    return , $ms.ToArray()
}

# --- app.ico ---
$ico = Join-Path $Root 'src\MilesFontExtractor\Assets\app.ico'
$sizes = 256, 64, 48, 32, 16
$frames = @(); foreach ($s in $sizes) { $frames += , (New-Frame $s) }
$fs = [System.IO.File]::Create($ico); $bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$off = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$frames[$i].Length); $bw.Write([uint32]$off)
    $off += $frames[$i].Length
}
foreach ($f in $frames) { $bw.Write($f) }
$bw.Close(); $fs.Close()

# --- logo.svg ---
$svg = @'
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100" width="128" height="128" role="img" aria-label="Miles Font Extractor">
  <defs>
    <linearGradient id="g" x1="0" y1="0" x2="1" y2="1">
      <stop offset="0" stop-color="#6C7CFF"/>
      <stop offset="1" stop-color="#A06CFF"/>
    </linearGradient>
  </defs>
  <rect width="100" height="100" rx="26" fill="url(#g)"/>
  <path d="M29 71V31L50 55L71 31V71" fill="none" stroke="#fff" stroke-width="9" stroke-linecap="round" stroke-linejoin="round"/>
</svg>
'@
New-Item -ItemType Directory -Force (Join-Path $Root 'docs') | Out-Null
Set-Content -Path (Join-Path $Root 'docs\logo.svg') -Value $svg -Encoding utf8

# --- logo PNGs (transparent background) ---
foreach ($px in 512, 1024) {
    $b = New-Object System.Drawing.Bitmap $px, $px, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $gg = [System.Drawing.Graphics]::FromImage($b); $gg.SmoothingMode = 'AntiAlias'; $gg.Clear([System.Drawing.Color]::Transparent)
    Draw-Mark $gg 0 0 $px; $gg.Dispose()
    $b.Save((Join-Path $Root "docs\logo-$px.png"), [System.Drawing.Imaging.ImageFormat]::Png); $b.Dispose()
}

# --- social-preview.png (1280x640, GitHub's recommended size) ---
$w = 1280; $h = 640
$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
$g.Clear([System.Drawing.Color]::FromArgb(255, 14, 16, 22))
# soft glow behind the mark
$glow = New-Object System.Drawing.Drawing2D.GraphicsPath
$glow.AddEllipse(($w / 2 - 330), 70, 660, 420)
$pgb = New-Object System.Drawing.Drawing2D.PathGradientBrush $glow
$pgb.CenterColor = [System.Drawing.Color]::FromArgb(70, 108, 124, 255)
$pgb.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 108, 124, 255))
$g.FillPath($pgb, $glow)
Draw-Mark $g ($w / 2 - 80) 160 160
$sf = New-Object System.Drawing.StringFormat; $sf.Alignment = 'Center'
$f1 = New-Object System.Drawing.Font 'Segoe UI Semibold', 60, ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
$f2 = New-Object System.Drawing.Font 'Segoe UI', 27, ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
$g.DrawString('Miles Font Extractor', $f1, [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF 0, 355, $w, 90), $sf)
$muted = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 139, 147, 167))
$g.DrawString('Find every font that is really yours. Leave Windows and programs alone.', $f2, $muted, (New-Object System.Drawing.RectangleF 0, 445, $w, 50), $sf)
$bmp.Save((Join-Path $Root 'docs\social-preview.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
"brand assets written"
