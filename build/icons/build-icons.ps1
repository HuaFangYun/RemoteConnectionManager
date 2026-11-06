<#
.SYNOPSIS
    Regenerates the RemoteConnectionManager icon set: per-size SVG masters, PNGs and the multi-size ICO.

.DESCRIPTION
    Same visual family as Drover: a near-black backing tile that owns its own contrast, a solid accent
    shape and a lighter outline shape. The mark is a remote screen (solid accent blue) on a stand, with
    a prompt chevron and cursor cut in white: one picture for both RDP sessions and shells.

    Small sizes (16, 20, 24, 32) are hand-tuned on the pixel grid so edges stay crisp. 40 and up are
    scaled from the 256 master. Rendering uses WPF, so no Python, Pillow or Inkscape is needed.

    Outputs:
      assets/icon/svg/rcm-{size}.svg     per-size SVG masters
      assets/icon/png/rcm-{size}.png     rendered PNGs
      assets/icon/rcm.svg                256 master, for the README and the web
      assets/logo/rcm-logo.svg           icon plus wordmark, dark text (light backgrounds)
      assets/logo/rcm-logo-dark.svg      icon plus wordmark, light text (dark backgrounds)
      src/RemoteConnectionManager.App/Resources/rcm.ico

.EXAMPLE
    pwsh build/icons/build-icons.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

$repo    = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$iconDir = Join-Path $repo 'assets\icon'
$logoDir = Join-Path $repo 'assets\logo'
$svgDir  = Join-Path $iconDir 'svg'
$pngDir  = Join-Path $iconDir 'png'
$icoPath = Join-Path $repo 'src\RemoteConnectionManager.App\Resources\rcm.ico'
foreach ($d in $svgDir, $pngDir, $logoDir, (Split-Path $icoPath)) { New-Item -ItemType Directory -Force $d | Out-Null }

# ---- Palette. Accent matches Chrome.Accent in Themes/Rcm.xaml; tile matches Drover's. ----
$Tile   = '#1a1a1a'
$Screen = '#2f7fd6'
$Stand  = '#8fc1f2'
$Glyph  = '#ffffff'

$Sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256

# ---- Geometry. All numbers are in pixels at the given size. ----
# tile: corner radius. screen: rect. neck/base: stand rects. chev: three points. cur: line ends. g: glyph stroke.
$Master = @{
    tileR = 46
    screen = @{ x = 28;  y = 44;  w = 200; h = 136; r = 18 }
    neck   = @{ x = 114; y = 180; w = 28;  h = 18;  r = 0 }
    base   = @{ x = 74;  y = 194; w = 108; h = 18;  r = 9 }
    chev   = @(@(72, 82), @(110, 112), @(72, 142))
    cur    = @(@(126, 142), @(178, 142))
    g      = 17
}

$HandTuned = @{
    16 = @{
        tileR = 3
        screen = @{ x = 1; y = 2; w = 14; h = 9; r = 1.5 }
        neck   = @{ x = 7; y = 11; w = 2; h = 1; r = 0 }
        base   = @{ x = 4; y = 12; w = 8; h = 2; r = 1 }
        chev   = @(@(4, 4), @(6.5, 6.25), @(4, 8.5))
        cur    = @(@(8, 8.5), @(11.5, 8.5))
        g      = 1.5
    }
    20 = @{
        tileR = 3.6
        screen = @{ x = 2; y = 3; w = 16; h = 11; r = 1.75 }
        neck   = @{ x = 9; y = 14; w = 2; h = 1; r = 0 }
        base   = @{ x = 5.5; y = 15; w = 9; h = 2; r = 1 }
        chev   = @(@(5.5, 5.75), @(8.5, 8.25), @(5.5, 10.75))
        cur    = @(@(10, 10.75), @(14, 10.75))
        g      = 1.6
    }
    24 = @{
        tileR = 4.3
        screen = @{ x = 2; y = 4; w = 20; h = 13; r = 2 }
        neck   = @{ x = 11; y = 17; w = 2; h = 1.5; r = 0 }
        base   = @{ x = 7; y = 18; w = 10; h = 2; r = 1 }
        chev   = @(@(6.5, 7), @(10, 10), @(6.5, 13))
        cur    = @(@(12, 13), @(17, 13))
        g      = 2
    }
    32 = @{
        tileR = 5.76
        screen = @{ x = 3; y = 5; w = 26; h = 18; r = 2.5 }
        neck   = @{ x = 14; y = 23; w = 4; h = 2; r = 0 }
        base   = @{ x = 9; y = 24.5; w = 14; h = 2.5; r = 1.25 }
        chev   = @(@(9, 9.5), @(13.5, 13.25), @(9, 17))
        cur    = @(@(15.5, 17), @(22, 17))
        g      = 2.4
    }
}

function Get-Spec([int]$size) {
    if ($HandTuned.ContainsKey($size)) { return $HandTuned[$size] }
    $k = $size / 256.0
    $scaleRect = { param($r) @{ x = $r.x * $k; y = $r.y * $k; w = $r.w * $k; h = $r.h * $k; r = $r.r * $k } }
    @{
        tileR  = $Master.tileR * $k
        screen = & $scaleRect $Master.screen
        neck   = & $scaleRect $Master.neck
        base   = & $scaleRect $Master.base
        chev   = $Master.chev | ForEach-Object { , @(($_[0] * $k), ($_[1] * $k)) }
        cur    = $Master.cur  | ForEach-Object { , @(($_[0] * $k), ($_[1] * $k)) }
        g      = $Master.g * $k
    }
}

function F([double]$v) { $v.ToString('0.###', [Globalization.CultureInfo]::InvariantCulture) }

# ---- SVG ----
function Get-SvgBody($s, [int]$size, [double]$dx = 0, [double]$dy = 0) {
    $rect = { param($r, $fill) "<rect x=`"$(F ($r.x + $dx))`" y=`"$(F ($r.y + $dy))`" width=`"$(F $r.w)`" height=`"$(F $r.h)`" rx=`"$(F $r.r)`" fill=`"$fill`"/>" }
    $pts = ($s.chev | ForEach-Object { "$(F ($_[0] + $dx)),$(F ($_[1] + $dy))" }) -join ' '
    @(
        "<rect x=`"$(F $dx)`" y=`"$(F $dy)`" width=`"$size`" height=`"$size`" rx=`"$(F $s.tileR)`" fill=`"$Tile`"/>"
        & $rect $s.screen $Screen
        & $rect $s.neck $Stand
        & $rect $s.base $Stand
        "<polyline points=`"$pts`" fill=`"none`" stroke=`"$Glyph`" stroke-width=`"$(F $s.g)`" stroke-linecap=`"round`" stroke-linejoin=`"round`"/>"
        "<line x1=`"$(F ($s.cur[0][0] + $dx))`" y1=`"$(F ($s.cur[0][1] + $dy))`" x2=`"$(F ($s.cur[1][0] + $dx))`" y2=`"$(F ($s.cur[1][1] + $dy))`" stroke=`"$Glyph`" stroke-width=`"$(F $s.g)`" stroke-linecap=`"round`"/>"
    )
}

function Write-Svg([int]$size, [string]$path) {
    $body = (Get-SvgBody (Get-Spec $size) $size | ForEach-Object { "  $_" }) -join "`n"
    $svg = "<svg xmlns=`"http://www.w3.org/2000/svg`" width=`"$size`" height=`"$size`" viewBox=`"0 0 $size $size`">`n$body`n</svg>`n"
    [IO.File]::WriteAllText($path, $svg)
}

# ---- WPF render ----
function Get-Brush([string]$hex) {
    $b = [Windows.Media.SolidColorBrush]::new([Windows.Media.ColorConverter]::ConvertFromString($hex))
    $b.Freeze(); $b
}

function Render-Png([int]$size) {
    $s = Get-Spec $size
    $dv = [Windows.Media.DrawingVisual]::new()
    $dc = $dv.RenderOpen()
    $rr = { param($r, $hex) $dc.DrawRoundedRectangle((Get-Brush $hex), $null, [Windows.Rect]::new($r.x, $r.y, $r.w, $r.h), $r.r, $r.r) }

    $dc.DrawRoundedRectangle((Get-Brush $Tile), $null, [Windows.Rect]::new(0, 0, $size, $size), $s.tileR, $s.tileR)
    & $rr $s.screen $Screen
    & $rr $s.neck $Stand
    & $rr $s.base $Stand

    $pen = [Windows.Media.Pen]::new((Get-Brush $Glyph), $s.g)
    $pen.StartLineCap = 'Round'; $pen.EndLineCap = 'Round'; $pen.LineJoin = 'Round'; $pen.Freeze()

    $fig = [Windows.Media.PathFigure]::new()
    $fig.StartPoint = [Windows.Point]::new($s.chev[0][0], $s.chev[0][1])
    $fig.IsClosed = $false
    $fig.Segments.Add([Windows.Media.LineSegment]::new([Windows.Point]::new($s.chev[1][0], $s.chev[1][1]), $true))
    $fig.Segments.Add([Windows.Media.LineSegment]::new([Windows.Point]::new($s.chev[2][0], $s.chev[2][1]), $true))
    $geo = [Windows.Media.PathGeometry]::new()
    $geo.Figures.Add($fig)
    $dc.DrawGeometry($null, $pen, $geo)
    $dc.DrawLine($pen, [Windows.Point]::new($s.cur[0][0], $s.cur[0][1]), [Windows.Point]::new($s.cur[1][0], $s.cur[1][1]))
    $dc.Close()

    $bmp = [Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($dv)
    $enc = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $ms = [IO.MemoryStream]::new()
    $enc.Save($ms)
    , $ms.ToArray()
}

# ---- ICO: PNG-compressed entries, supported by Windows Vista and later. ----
function Write-Ico([hashtable]$pngs, [string]$path) {
    $order = $pngs.Keys | Sort-Object
    $ms = [IO.MemoryStream]::new()
    $w = [IO.BinaryWriter]::new($ms)
    $w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$order.Count)
    $offset = 6 + 16 * $order.Count
    foreach ($size in $order) {
        $data = $pngs[$size]
        $dim = if ($size -ge 256) { 0 } else { $size }
        $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([uint16]1); $w.Write([uint16]32)
        $w.Write([uint32]$data.Length); $w.Write([uint32]$offset)
        $offset += $data.Length
    }
    foreach ($size in $order) { $w.Write([byte[]]$pngs[$size]) }
    $w.Flush()
    [IO.File]::WriteAllBytes($path, $ms.ToArray())
}

# ---- Logo lockups: 256 master scaled into a 64 high badge plus a wordmark. ----
function Write-Logo([string]$path, [string]$textColour, [string]$subColour) {
    $k = 64 / 256.0
    $body = (Get-SvgBody (Get-Spec 256) 256 | ForEach-Object { "    $_" }) -join "`n"
    $svg = @"
<svg xmlns="http://www.w3.org/2000/svg" width="420" height="64" viewBox="0 0 420 64">
  <g transform="scale($k)">
$body
  </g>
  <text x="80" y="30" font-family="Segoe UI Variable Display, Segoe UI, system-ui, sans-serif" font-size="24" font-weight="600" fill="$textColour">Remote Connection Manager</text>
  <text x="81" y="52" font-family="Segoe UI, system-ui, sans-serif" font-size="13" letter-spacing="0.6" fill="$subColour">RDP · SHELLS · KEY VAULT</text>
</svg>
"@
    [IO.File]::WriteAllText($path, $svg)
}

$pngs = @{}
foreach ($size in $Sizes) {
    Write-Svg $size (Join-Path $svgDir "rcm-$size.svg")
    $bytes = Render-Png $size
    [IO.File]::WriteAllBytes((Join-Path $pngDir "rcm-$size.png"), $bytes)
    $pngs[$size] = $bytes
}
Copy-Item (Join-Path $svgDir 'rcm-256.svg') (Join-Path $iconDir 'rcm.svg') -Force
Write-Ico $pngs $icoPath
Write-Logo (Join-Path $logoDir 'rcm-logo.svg') '#1e1e1e' '#5c6670'
Write-Logo (Join-Path $logoDir 'rcm-logo-dark.svg') '#e6e6e6' '#8fc1f2'

Write-Host "Icons written: $($Sizes.Count) sizes -> $icoPath"
