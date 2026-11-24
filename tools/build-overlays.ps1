<#
.SYNOPSIS
    Merges all four realHfde Essential Dark layouts (FPS, Sleek, Sleek Compact, Performance) into
    one combined OverlayEditor layout (essential_tsume.ovl) and one stitched PNG atlas
    (essential_tsume_atlas.png), reworked for single-chip handheld PCs with one shared temperature
    reading.

.DESCRIPTION
    Each level's layers are gated with VisibilitySource backed by AIDA64_SensorValues
    (TSUME_OVERLAY_LEVEL with Formula "x == N") and positioned live via TSUME_POS_X / TSUME_POS_Y.
    Tsume switches levels (including Off) and placement live without ever restarting RTSS (which
    could otherwise crash hooked DX12/Vulkan games mid-session). When Tsume exits (mapping
    missing), every visibility source invalidates to FLT_MAX and nothing is drawn.

    Single-chip handheld PCs (APU/SoC) share one die between CPU and GPU, so every
    telemetry layout displays one unified temperature reading instead of duplicating
    CPU/GPU temperatures (and omits discrete GDDR VRAM temperature):
      - Level 1 (FPS):    Compact FPS square
      - Level 2 (Bar):    Symmetrical CPU (ring + W) | FPS + 1% / Temp | GPU (W + ring)
      - Level 3 (Detail): CPU (ring + stats) | GPU (ring + stats) | FPS + Temp + lows + graph
      - Level 4 (Full):   Side panel with FPS + TEMP header, then CPU and GPU sections

    Temperature, CPU power, GPU power, CPU clock, and GPU VRAM prefer OverlayEditor's
    built-in HAL source when valid (and > 0 for VRAM, since unified-memory iGPUs report
    0 dedicated VRAM) and fall back to Tsume's AIDA64 readings (EC + PDH) when HAL
    reports blank or zero values.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repoRoot = Split-Path -Parent $PSScriptRoot
$essentialRoot = Join-Path $repoRoot 'src\Tsume.App\Overlays\Essential'
$darkDir = Join-Path $essentialRoot 'Dark'
$enc = [System.Text.Encoding]::GetEncoding(1252)
$maxLayers = 256
$deg = [char]0xB0
$degC = "${deg}C"

# Dark theme palette.
$valueHex   = 'FFFFFF'
$mutedHex   = 'D1D1D6'
$cpuHex     = '0A84FF'
$gpuHex     = 'BF5AF2'
$fpsHex     = '30D158'
$tempColor  = 'FFFFFF,,1,FF9F0A,1,2,FF453A,2,(Temperature tier,1)'

$ringCpuColor = [System.Drawing.Color]::FromArgb(255, 10, 132, 255)
$ringGpuColor = [System.Drawing.Color]::FromArgb(255, 191, 90, 242)

# High-resolution ring sprites (145 frames = 2.5 deg / ~1.2 px per step; exact 0/25/50/75/100% frames)
# animated at ~30 FPS (RefreshPeriod=33 on ring layers) via a two-stage critically damped EMA on
# HAL/Timer, while all hardware sensors stay strictly at RTSS's standard 1-second cadence.
$ringFrames  = 145
$ringPerLine = 29
$ringRows    = [int][Math]::Ceiling($ringFrames / [double]$ringPerLine)
$ringMax     = '100.6944'
$ringBias    = '0.5'
$ringAlpha   = '0.28'
$ringRefresh = 33
$baseRefresh = 1000

# Reworked panel chrome dimensions.
$fpsPanelW     = 72;  $fpsPanelH     = 72
$sleekPanelW   = 452; $sleekPanelH   = 72;  $sleekDividers   = @(144, 308)
$compactPanelW = 686; $compactPanelH = 93;  $compactDividers = @(232, 464)
$perfPanelW    = 225; $perfPanelH    = 504; $perfDividers    = @(168, 304)

# Crisp native Raster 3D master font: Segoe UI is built into every Windows PC with full LCD hinting,
# and using only Size=100 (default, 14px) and Size=200 (28px) hits RTSS's pre-rasterized 100%/200%
# font charsets at exact 1:1 pixel mapping with zero bilinear scaling blur.
$masterFontFace   = 'Segoe UI'
$masterFontHeight = -14
$masterFontWeight = 700
$masterZoomRatio  = 1

function Read-IniSections([string]$path) {
    $lines = [System.IO.File]::ReadAllLines($path, $enc)
    $sections = [System.Collections.Generic.List[object]]::new()
    $current = $null

    foreach ($line in $lines) {
        if ($line -match '^\[(.+)\]$') {
            $current = [PSCustomObject]@{
                Name  = $Matches[1]
                Lines = [System.Collections.Generic.List[string]]::new()
            }
            $sections.Add($current)
        }
        elseif ($null -ne $current) {
            $current.Lines.Add($line)
        }
    }

    return $sections
}

function Draw-FpsSquarePanel(
    [System.Drawing.Graphics]$target,
    [System.Drawing.Bitmap]$sourceAtlas,
    [int]$srcH,
    [int]$cleanColX,
    [int]$destX,
    [int]$destY,
    [int]$width,
    [int]$height,
    [float]$radius) {

    $tile = New-Object System.Drawing.Bitmap($width, $height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    try {
        $d = $radius * 2.0
        $path.AddArc(0.5, 0.5, $d, $d, 180, 90)
        $path.AddArc(($width - $d - 0.5), 0.5, $d, $d, 270, 90)
        $path.AddArc(($width - $d - 0.5), ($height - $d - 0.5), $d, $d, 0, 90)
        $path.AddArc(0.5, ($height - $d - 0.5), $d, $d, 90, 90)
        $path.CloseFigure()

        # Sample the clean vertical fill gradient into a 1px strip, then fill the clipped square.
        $fillBmp = New-Object System.Drawing.Bitmap($width, $height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $fg = [System.Drawing.Graphics]::FromImage($fillBmp)
            try {
                $fg.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
                $fg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBilinear
                $fg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
                $fg.DrawImage($sourceAtlas,
                    (New-Object System.Drawing.Rectangle(0, 0, $width, $height)),
                    (New-Object System.Drawing.Rectangle($cleanColX, 2, 1, ($srcH - 4))),
                    [System.Drawing.GraphicsUnit]::Pixel)
            }
            finally { $fg.Dispose() }

            $brush = New-Object System.Drawing.TextureBrush($fillBmp)
            $borderColor = $sourceAtlas.GetPixel($cleanColX, 1)
            $pen = New-Object System.Drawing.Pen($borderColor, 1.0)
            try {
                $tg = [System.Drawing.Graphics]::FromImage($tile)
                try {
                    $tg.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
                    $tg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                    $tg.FillPath($brush, $path)
                    if ($borderColor.A -gt 0) {
                        $tg.DrawPath($pen, $path)
                    }
                }
                finally { $tg.Dispose() }
            }
            finally {
                $pen.Dispose()
                $brush.Dispose()
            }
        }
        finally { $fillBmp.Dispose() }

        $target.DrawImageUnscaled($tile, $destX, $destY)
    }
    finally {
        $path.Dispose()
        $tile.Dispose()
    }
}

function Draw-HorizontalPanel(
    [System.Drawing.Graphics]$target,
    [System.Drawing.Bitmap]$sourceAtlas,
    [int]$srcW,
    [int]$height,
    [int]$capW,
    [int]$cleanColX,
    [int]$srcDivCenterX,
    [int]$divHalfW,
    [int]$destX,
    [int]$destY,
    [int]$newW,
    [int[]]$dividerCenters) {

    # Preserve exact left and right rounded caps from the upstream Dark atlas.
    $target.DrawImage($sourceAtlas,
        (New-Object System.Drawing.Rectangle($destX, $destY, $capW, $height)),
        (New-Object System.Drawing.Rectangle(0, 0, $capW, $height)),
        [System.Drawing.GraphicsUnit]::Pixel)

    # Tile a clean divider-free 1px column across the middle.
    $colRect = New-Object System.Drawing.Rectangle($cleanColX, 0, 1, $height)
    for ($x = $capW; $x -lt ($newW - $capW); $x++) {
        $target.DrawImage($sourceAtlas,
            (New-Object System.Drawing.Rectangle(($destX + $x), $destY, 1, $height)),
            $colRect,
            [System.Drawing.GraphicsUnit]::Pixel)
    }

    $target.DrawImage($sourceAtlas,
        (New-Object System.Drawing.Rectangle(($destX + $newW - $capW), $destY, $capW, $height)),
        (New-Object System.Drawing.Rectangle(($srcW - $capW), 0, $capW, $height)),
        [System.Drawing.GraphicsUnit]::Pixel)

    # Stamp the exact vertical divider strip at each section boundary.
    $divW = $divHalfW * 2 + 1
    $divSrcRect = New-Object System.Drawing.Rectangle(($srcDivCenterX - $divHalfW), 0, $divW, $height)
    foreach ($cx in $dividerCenters) {
        $target.DrawImage($sourceAtlas,
            (New-Object System.Drawing.Rectangle(($destX + $cx - $divHalfW), $destY, $divW, $height)),
            $divSrcRect,
            [System.Drawing.GraphicsUnit]::Pixel)
    }
}

function Draw-VerticalPerfPanel(
    [System.Drawing.Graphics]$target,
    [System.Drawing.Bitmap]$sourceAtlas,
    [int]$destX,
    [int]$destY,
    [int]$newH,
    [int[]]$dividerCenters) {

    $w = $sourceAtlas.Width
    $srcH = 544
    $capH = 24
    $divHalfH = 3
    $oldDivs = @(165, 315)

    $clean = New-Object System.Drawing.Bitmap($w, $srcH, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $resized = New-Object System.Drawing.Bitmap($w, $newH, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $cg = [System.Drawing.Graphics]::FromImage($clean)
        try {
            $cg.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
            $cg.DrawImage($sourceAtlas,
                (New-Object System.Drawing.Rectangle(0, 0, $w, $srcH)),
                (New-Object System.Drawing.Rectangle(0, 0, $w, $srcH)),
                [System.Drawing.GraphicsUnit]::Pixel)
        }
        finally { $cg.Dispose() }

        # Interpolate across the two original divider bands to remove them cleanly.
        foreach ($dy in $oldDivs) {
            $y0 = $dy - $divHalfH - 1
            $y1 = $dy + $divHalfH + 1
            $span = [double]($y1 - $y0)
            for ($x = 0; $x -lt $w; $x++) {
                $c0 = $sourceAtlas.GetPixel($x, $y0)
                $c1 = $sourceAtlas.GetPixel($x, $y1)
                for ($y = $y0 + 1; $y -lt $y1; $y++) {
                    $t = ($y - $y0) / $span
                    $a = [int][Math]::Round($c0.A + ($c1.A - $c0.A) * $t)
                    $r = [int][Math]::Round($c0.R + ($c1.R - $c0.R) * $t)
                    $g = [int][Math]::Round($c0.G + ($c1.G - $c0.G) * $t)
                    $b = [int][Math]::Round($c0.B + ($c1.B - $c0.B) * $t)
                    $clean.SetPixel($x, $y, [System.Drawing.Color]::FromArgb($a, $r, $g, $b))
                }
            }
        }

        $rg = [System.Drawing.Graphics]::FromImage($resized)
        try {
            $rg.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
            $rg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBilinear
            $rg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half

            # Top cap untouched.
            $rg.DrawImage($clean,
                (New-Object System.Drawing.Rectangle(0, 0, $w, $capH)),
                (New-Object System.Drawing.Rectangle(0, 0, $w, $capH)),
                [System.Drawing.GraphicsUnit]::Pixel)

            # Scale middle body vertically (1:1 horizontally keeps side borders pixel-sharp).
            $bodySrcH = $srcH - $capH * 2
            $bodyDstH = $newH - $capH * 2
            $rg.DrawImage($clean,
                (New-Object System.Drawing.Rectangle(0, $capH, $w, $bodyDstH)),
                (New-Object System.Drawing.Rectangle(0, $capH, $w, $bodySrcH)),
                [System.Drawing.GraphicsUnit]::Pixel)

            # Bottom cap untouched.
            $rg.DrawImage($clean,
                (New-Object System.Drawing.Rectangle(0, ($newH - $capH), $w, $capH)),
                (New-Object System.Drawing.Rectangle(0, ($srcH - $capH), $w, $capH)),
                [System.Drawing.GraphicsUnit]::Pixel)
        }
        finally { $rg.Dispose() }

        # Re-apply the exact horizontal divider delta at each new divider Y.
        $refDivY = $oldDivs[0]
        foreach ($newDivY in $dividerCenters) {
            for ($dy = -$divHalfH; $dy -le $divHalfH; $dy++) {
                $srcY = $refDivY + $dy
                $dstY = $newDivY + $dy
                for ($x = 12; $x -lt ($w - 12); $x++) {
                    $orig = $sourceAtlas.GetPixel($x, $srcY)
                    $base = $clean.GetPixel($x, $srcY)
                    if ($orig -eq $base) { continue }
                    $cur = $resized.GetPixel($x, $dstY)
                    $a = [Math]::Max(0, [Math]::Min(255, $cur.A + ($orig.A - $base.A)))
                    $r = [Math]::Max(0, [Math]::Min(255, $cur.R + ($orig.R - $base.R)))
                    $g = [Math]::Max(0, [Math]::Min(255, $cur.G + ($orig.G - $base.G)))
                    $b = [Math]::Max(0, [Math]::Min(255, $cur.B + ($orig.B - $base.B)))
                    $resized.SetPixel($x, $dstY, [System.Drawing.Color]::FromArgb($a, $r, $g, $b))
                }
            }
        }

        $target.DrawImageUnscaled($resized, $destX, $destY)
    }
    finally {
        $resized.Dispose()
        $clean.Dispose()
    }
}

# Draws 145-frame anti-aliased colored progress arcs directly on top of the Dark Frame-0 ring track
# so each ring renders in a single sprite layer.
function Draw-RingGrid(
    [System.Drawing.Graphics]$target,
    [System.Drawing.Bitmap]$sourceAtlas,
    [int]$trackSrcX,
    [int]$trackSrcY,
    [int]$size,
    [float]$radius,
    [float]$strokeWidth,
    [System.Drawing.Color]$color,
    [int]$destX,
    [int]$destY,
    [int]$frames,
    [int]$perLine) {

    $cell = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $pen  = New-Object System.Drawing.Pen($color, $strokeWidth)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round

    $center  = $size / 2.0
    $rect    = New-Object System.Drawing.RectangleF(($center - $radius), ($center - $radius), (2.0 * $radius), (2.0 * $radius))
    $srcRect = New-Object System.Drawing.Rectangle($trackSrcX, $trackSrcY, $size, $size)
    $dstRect = New-Object System.Drawing.Rectangle(0, 0, $size, $size)

    try {
        $cg = [System.Drawing.Graphics]::FromImage($cell)
        try {
            $cg.SmoothingMode      = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
            $cg.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $cg.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality

            for ($i = 0; $i -lt $frames; $i++) {
                $cg.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
                $cg.Clear([System.Drawing.Color]::Transparent)
                $cg.DrawImage($sourceAtlas, $dstRect, $srcRect, [System.Drawing.GraphicsUnit]::Pixel)

                if ($i -eq ($frames - 1)) {
                    $cg.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceOver
                    $cg.DrawEllipse($pen, $rect)
                }
                elseif ($i -gt 0) {
                    $sweep = [single](360.0 * $i / ($frames - 1))
                    $cg.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceOver
                    $cg.DrawArc($pen, $rect, -90.0, $sweep)
                }

                $col = $i % $perLine
                $row = [int][Math]::Floor($i / $perLine)
                $target.DrawImageUnscaled($cell, $destX + $col * $size, $destY + $row * $size)
            }
        }
        finally { $cg.Dispose() }
    }
    finally {
        $pen.Dispose()
        $cell.Dispose()
    }
}

function New-SourceLines([string]$name, [string]$units, [string]$format, [string]$formula, [string]$provider, [string]$id) {
    # Win32 GetPrivateProfileString strips matching leading and trailing double quotes from INI values;
    # wrap any such formula in parentheses so OverlayEditor never sees broken quotes and falls back to raw x.
    if ($formula.Length -ge 2 -and $formula.StartsWith('"') -and $formula.EndsWith('"')) {
        $formula = "($formula)"
    }
    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("Name=$name")
    $lines.Add("Units=$units")
    $lines.Add("Format=$format")
    $lines.Add("Formula=$formula")
    $lines.Add("Provider=$provider")
    $lines.Add("ID=$id")
    return $lines.ToArray()
}

function Format-DynamicPos([System.Collections.Generic.HashSet[int]]$usedX, [System.Collections.Generic.HashSet[int]]$usedY, [int]$layerIdx, [int]$dx, [int]$dy, [int]$ex, [int]$ey, [int]$origin) {
    [void]$usedX.Add($dx)
    [void]$usedY.Add($dy)
    # In RTSS hypertext, <P=x,y> MUST precede <L...> so the layer origin and <E=w,h,origin> box
    # anchor at (x, y) instead of OverlayEditor's static (0, 0) layer header.
    $tag = "<P=%PX_${dx}%,%PY_${dy}%><L$layerIdx>"
    if ($ex -gt 0 -and $ey -gt 0) {
        if ($origin -ne 0) { $tag += "<E=$ex,$ey,$origin>" }
        else { $tag += "<E=$ex,$ey>" }
    }
    return $tag
}

function Add-SpriteLayer(
    [System.Collections.Generic.List[string[]]]$list,
    [System.Collections.Generic.HashSet[int]]$usedX,
    [System.Collections.Generic.HashSet[int]]$usedY,
    [string]$vis,
    [string]$name,
    [int]$dx, [int]$dy,
    [int]$sx, [int]$sy, [int]$sw, [int]$sh,
    [int]$atlasW, [int]$atlasH) {

    if ($sx + $sw -gt $atlasW -or $sy + $sh -gt $atlasH) {
        throw "Sprite '$name' out of stitched atlas bounds: x=$sx y=$sy ${sw}x${sh} vs ${atlasW}x${atlasH}"
    }
    $pos = Format-DynamicPos $usedX $usedY $list.Count $dx $dy $sw $sh 0
    $list.Add(@(
        "Name=$name"
        "VisibilitySource=$vis"
        "Text=$pos<I=$sw,$sh,$sx,$sy,$sw,$sh>"
        "PositionX=0"
        "PositionY=0"
        "ExtentX=0"
        "ExtentY=0"
        "ExtentOrigin=0"
    ))
}

function Add-RingLayer(
    [System.Collections.Generic.List[string[]]]$list,
    [System.Collections.Generic.HashSet[int]]$usedX,
    [System.Collections.Generic.HashSet[int]]$usedY,
    [string]$vis,
    [string]$name,
    [hashtable]$ring,
    [int]$dx, [int]$dy,
    [int]$atlasW, [int]$atlasH) {

    $gridW = $ring.PerLine * $ring.Size
    $gridH = [int][Math]::Ceiling($ring.Frames / [double]$ring.PerLine) * $ring.Size
    if ($ring.X + $gridW -gt $atlasW -or $ring.Y + $gridH -gt $atlasH) {
        throw "Ring '$name' out of stitched atlas bounds: x=$($ring.X) y=$($ring.Y) ${gridW}x${gridH} vs ${atlasW}x${atlasH}"
    }
    $pos = Format-DynamicPos $usedX $usedY $list.Count $dx $dy $ring.Size $ring.Size 0
    $list.Add(@(
        "Name=$name"
        "VisibilitySource=$vis"
        "Text=$pos<AI=$($ring.Source),$($ring.Size),$($ring.Size),0,$($ring.Max),$($ring.X),$($ring.Y),$($ring.Size),$($ring.Size),$($ring.Frames),$($ring.PerLine),$($ring.Bias)>"
        "PositionX=0"
        "PositionY=0"
        "ExtentX=0"
        "ExtentY=0"
        "ExtentOrigin=0"
        "RefreshPeriod=$ringRefresh"
    ))
}

function Add-GraphLayer(
    [System.Collections.Generic.List[string[]]]$list,
    [System.Collections.Generic.HashSet[int]]$usedX,
    [System.Collections.Generic.HashSet[int]]$usedY,
    [string]$vis,
    [string]$name,
    [string]$graphTag,
    [int]$dx, [int]$dy, [int]$ex, [int]$ey,
    [string]$color) {

    $pos = Format-DynamicPos $usedX $usedY $list.Count $dx $dy $ex $ey 0
    $list.Add(@(
        "Name=$name"
        "VisibilitySource=$vis"
        "Text=$pos$graphTag"
        "PositionX=0"
        "PositionY=0"
        "ExtentX=0"
        "ExtentY=0"
        "ExtentOrigin=0"
        "TextColor=$color"
    ))
}

function Add-TextLayer(
    [System.Collections.Generic.List[string[]]]$list,
    [System.Collections.Generic.HashSet[int]]$usedX,
    [System.Collections.Generic.HashSet[int]]$usedY,
    [string]$vis,
    [string]$name,
    [string]$text,
    [int]$dx, [int]$dy, [int]$ex, [int]$ey, [int]$origin,
    [int]$size,
    [string]$color) {

    $pos = Format-DynamicPos $usedX $usedY $list.Count $dx $dy $ex $ey $origin
    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("Name=$name")
    $lines.Add("VisibilitySource=$vis")
    $lines.Add("Text=$pos$text")
    $lines.Add("PositionX=0")
    $lines.Add("PositionY=0")
    $lines.Add("ExtentX=0")
    $lines.Add("ExtentY=0")
    $lines.Add("ExtentOrigin=0")
    if ($size -ne 100) { $lines.Add("Size=$size") }
    $lines.Add("TextColor=$color")
    $list.Add($lines.ToArray())
}

# 1. Build the Dark stitched atlas (1943x1804, leaving 244 px of vertical headroom inside RTSS's
#    shared 2048x2048 Raster 3D font+atlas texture so CharsetSize0=200 / CharsetSize1=100 /
#    CharsetSize2=50 glyphs at the top of the texture are never overwritten by the embedded atlas):
#    - Top (y=0..504):       Full panel (0,0), Detail panel (229,0), Bar panel (919,0), FPS panel (1375,0)
#    - Bottom (y=508..1804): 4 x 145-frame Dark rings (Bar CPU/GPU 62x62, Detail CPU/GPU 67x67)
$outPngName = 'essential_tsume_atlas.png'
$outPngPath = Join-Path $essentialRoot $outPngName
$outOvlPath = Join-Path $essentialRoot 'essential_tsume.ovl'

$sleekRingSz   = 62
$compactRingSz = 67

$perfPanelX    = 0;    $perfPanelY    = 0
$compactPanelX = 229;  $compactPanelY = 0
$sleekPanelX   = 919;  $sleekPanelY   = 0
$fpsPanelX     = 1375; $fpsPanelY     = 0

$sleekCpuRingY   = $perfPanelH + 4                               # 508
$sleekGpuRingY   = $sleekCpuRingY   + ($ringRows * $sleekRingSz)   + 2
$compactCpuRingY = $sleekGpuRingY   + ($ringRows * $sleekRingSz)   + 2
$compactGpuRingY = $compactCpuRingY + ($ringRows * $compactRingSz) + 2

$totalWidth  = $ringPerLine * $compactRingSz                   # 1943
$totalHeight = $compactGpuRingY + ($ringRows * $compactRingSz) # 1804

# RTSS packs Raster 3D font charsets (200%/100%/50%) at the top of its 2048x2048 texture and blits
# the embedded atlas at the bottom; keep height <= 1850 so >= 198 px always remain for font glyphs.
$maxAtlasHeight = 1850
if ($totalWidth -gt 2048 -or $totalHeight -gt $maxAtlasHeight) {
    throw "Dark atlas ${totalWidth}x${totalHeight} exceeds safe RTSS Raster 3D budget (2048x${maxAtlasHeight})"
}

$imgSleek   = [System.Drawing.Bitmap]::FromFile((Join-Path $darkDir 'essential_sleek_atlas.png'))
$imgCompact = [System.Drawing.Bitmap]::FromFile((Join-Path $darkDir 'essential_sleek_compact_atlas.png'))
$imgPerf    = [System.Drawing.Bitmap]::FromFile((Join-Path $darkDir 'essential_performance_overlay_atlas.png'))
try {
    $stitched = New-Object System.Drawing.Bitmap($totalWidth, $totalHeight, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $g = [System.Drawing.Graphics]::FromImage($stitched)
        try {
            $g.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
            $g.Clear([System.Drawing.Color]::Transparent)

            Draw-VerticalPerfPanel $g $imgPerf $perfPanelX $perfPanelY $perfPanelH $perfDividers
            Draw-HorizontalPanel   $g $imgCompact 750 $compactPanelH 24 100 267 3 $compactPanelX $compactPanelY $compactPanelW $compactDividers
            Draw-HorizontalPanel   $g $imgSleek   480 $sleekPanelH   36 100 142 3 $sleekPanelX   $sleekPanelY   $sleekPanelW   $sleekDividers
            Draw-FpsSquarePanel    $g $imgCompact $compactPanelH 100 $fpsPanelX $fpsPanelY $fpsPanelW $fpsPanelH 8.0

            Draw-RingGrid $g $imgSleek   0 74  $sleekRingSz   28.05 4.10 $ringCpuColor 0 $sleekCpuRingY   $ringFrames $ringPerLine
            Draw-RingGrid $g $imgSleek   0 324 $sleekRingSz   28.05 4.10 $ringGpuColor 0 $sleekGpuRingY   $ringFrames $ringPerLine
            Draw-RingGrid $g $imgCompact 0 95  $compactRingSz 30.49 4.35 $ringCpuColor 0 $compactCpuRingY $ringFrames $ringPerLine
            Draw-RingGrid $g $imgCompact 0 365 $compactRingSz 30.49 4.35 $ringGpuColor 0 $compactGpuRingY $ringFrames $ringPerLine
        }
        finally { $g.Dispose() }

        $stitched.Save($outPngPath, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally { $stitched.Dispose() }
}
finally {
    $imgSleek.Dispose()
    $imgCompact.Dispose()
    $imgPerf.Dispose()
}

$sleekCpuRing   = @{ Source = 'CPU usage smooth';  X = 0; Y = $sleekCpuRingY;   Size = $sleekRingSz;   Frames = $ringFrames; PerLine = $ringPerLine; Max = $ringMax; Bias = $ringBias }
$sleekGpuRing   = @{ Source = 'GPU1 usage smooth'; X = 0; Y = $sleekGpuRingY;   Size = $sleekRingSz;   Frames = $ringFrames; PerLine = $ringPerLine; Max = $ringMax; Bias = $ringBias }
$compactCpuRing = @{ Source = 'CPU usage smooth';  X = 0; Y = $compactCpuRingY; Size = $compactRingSz; Frames = $ringFrames; PerLine = $ringPerLine; Max = $ringMax; Bias = $ringBias }
$compactGpuRing = @{ Source = 'GPU1 usage smooth'; X = 0; Y = $compactGpuRingY; Size = $compactRingSz; Frames = $ringFrames; PerLine = $ringPerLine; Max = $ringMax; Bias = $ringBias }

$perfSections = Read-IniSections (Join-Path $darkDir 'essential_performance_overlay.ovl')

# 2. Build combined sources (all hardware sensors polled once per second at [Settings] RefreshPeriod=1000):
#    - 4 level visibility sources backed by TSUME_OVERLAY_LEVEL
#    - 5 raw HAL sources (updated BEFORE AIDA in OverlayDataSourcesList::UpdateSources)
#    - 5 effective AIDA sources that use HAL when valid, falling back to Tsume AIDA values
#    - remaining HAL sources from essential_performance_overlay (excluding removed GPU/VRAM temps)
#    - rolling 1% / 0.1% low from RTSS's 1024-frame Frametime ring buffer
#    - two-stage critically damped EMA sources (HAL/Timer) for smooth 30 FPS ring animation
$sources = [System.Collections.Generic.List[string[]]]::new()

for ($lvl = 1; $lvl -le 4; $lvl++) {
    $sources.Add((New-SourceLines "Tsume level $lvl visible" '' '%.0f' "x == $lvl" 'AIDA' 'TSUME_OVERLAY_LEVEL'))
}

$fallbackSpecs = @(
    @{ Display = 'CPU temperature';   Raw = 'HAL CPU temperature';   Units = $degC; Format = '';     Scale = '';        HalId = 'CPU temperature';   AidaId = 'TSUME_CPU_TEMP';  RequirePositive = $false }
    @{ Display = 'CPU power';         Raw = 'HAL CPU power';         Units = 'W';   Format = '';     Scale = '';        HalId = 'CPU power';         AidaId = 'TSUME_CPU_POWER'; RequirePositive = $false }
    @{ Display = 'CPU clock';         Raw = 'HAL CPU clock';         Units = 'GHz'; Format = '%.2f'; Scale = ' / 1000'; HalId = 'CPU clock';         AidaId = 'TSUME_CPU_CLOCK'; RequirePositive = $false }
    @{ Display = 'GPU1 power';        Raw = 'HAL GPU1 power';        Units = 'W';   Format = '';     Scale = '';        HalId = 'GPU1 power';        AidaId = 'TSUME_GPU_POWER'; RequirePositive = $false }
    @{ Display = 'GPU1 memory usage'; Raw = 'HAL GPU1 memory usage'; Units = 'GB';  Format = '%.1f'; Scale = ' / 1024'; HalId = 'GPU1 memory usage'; AidaId = 'TSUME_GPU_VRAM';  RequirePositive = $true  }
)
$skipSourceNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($spec in $fallbackSpecs) { [void]$skipSourceNames.Add($spec.Display) }
[void]$skipSourceNames.Add('GPU1 temperature')
[void]$skipSourceNames.Add('GPU mem temp')

foreach ($spec in $fallbackSpecs) {
    $sources.Add((New-SourceLines $spec.Raw $spec.Units '' '' 'HAL' $spec.HalId))
}
foreach ($spec in $fallbackSpecs) {
    $halRef = "`"$($spec.Raw)`""
    $expr = if ($spec.RequirePositive) {
        "validate($halRef) ? ($halRef > 0 ? $halRef : x) : x"
    } else {
        "validate($halRef) ? $halRef : x"
    }
    $formula = "($expr)$($spec.Scale)"
    $sources.Add((New-SourceLines $spec.Display $spec.Units $spec.Format $formula 'AIDA' $spec.AidaId))
}

# Per-chip temperature color thresholds published by Tsume (default 75/85 C; Intel handhelds 85/95 C).
$sources.Add((New-SourceLines 'Temp warn threshold' $degC '%.0f' 'validate(x) ? x : 75' 'AIDA' 'TSUME_TEMP_WARN'))
$sources.Add((New-SourceLines 'Temp hot threshold'  $degC '%.0f' 'validate(x) ? x : 85' 'AIDA' 'TSUME_TEMP_HOT'))
$sources.Add((New-SourceLines 'Temperature tier'    ''    '%.0f' 'validate("CPU temperature") ? ("CPU temperature" >= "Temp hot threshold" ? 2 : ("CPU temperature" >= "Temp warn threshold" ? 1 : 0)) : 0' 'AIDA' 'TSUME_OVERLAY_LEVEL'))

foreach ($sec in ($perfSections | Where-Object { $_.Name -match '^Source\d+$' })) {
    $nameLine = $sec.Lines | Where-Object { $_ -match '^Name=' } | Select-Object -First 1
    $srcName = if ($nameLine) { $nameLine.Substring(5) } else { '' }
    if ($skipSourceNames.Contains($srcName)) { continue }
    $sources.Add($sec.Lines.ToArray())
}

# Live rolling 1% low and 0.1% low from RTSS's always-active 1024-frame Frametime ring buffer,
# updated once per second alongside all other telemetry sources.
$sources.Add((New-SourceLines 'Framerate 1 Low'  'FPS' '%.0f' '1000 / percentile("Frametime", 99)'   'HAL' 'Stub'))
$sources.Add((New-SourceLines 'Framerate 01 Low' 'FPS' '%.0f' '1000 / percentile("Frametime", 99.9)' 'HAL' 'Stub'))

# Smooth 30 FPS resource ring sources (two-stage critically damped EMA on HAL/Timer, zero extra sensor polling).
$sources.Add((New-SourceLines 'CPU usage stage1'  '%' '' "if(validate(`"CPU usage stage1`"), `"CPU usage stage1`" + (`"CPU usage`" - `"CPU usage stage1`") * $ringAlpha, `"CPU usage`")"          'HAL' 'Timer'))
$sources.Add((New-SourceLines 'CPU usage smooth'  '%' '' "if(validate(`"CPU usage smooth`"), `"CPU usage smooth`" + (`"CPU usage stage1`" - `"CPU usage smooth`") * $ringAlpha, `"CPU usage`")"   'HAL' 'Timer'))
$sources.Add((New-SourceLines 'GPU1 usage stage1' '%' '' "if(validate(`"GPU1 usage stage1`"), `"GPU1 usage stage1`" + (`"GPU1 usage`" - `"GPU1 usage stage1`") * $ringAlpha, `"GPU1 usage`")"     'HAL' 'Timer'))
$sources.Add((New-SourceLines 'GPU1 usage smooth' '%' '' "if(validate(`"GPU1 usage smooth`"), `"GPU1 usage smooth`" + (`"GPU1 usage stage1`" - `"GPU1 usage smooth`") * $ringAlpha, `"GPU1 usage`")" 'HAL' 'Timer'))

# 3. Build layers for all four levels using relative offsets from TSUME_POS_X / TSUME_POS_Y.
$mergedLayers = [System.Collections.Generic.List[string[]]]::new()
$usedRelX     = [System.Collections.Generic.HashSet[int]]::new()
$usedRelY     = [System.Collections.Generic.HashSet[int]]::new()

# ── Level 1: Compact FPS square (72x72) ───────────────────────────────────────────────────────────
$vis1 = 'Tsume level 1 visible'
Add-SpriteLayer $mergedLayers $usedRelX $usedRelY $vis1 'Panel' 0 0 $fpsPanelX $fpsPanelY $fpsPanelW $fpsPanelH $totalWidth $totalHeight
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis1 'FPS label' 'FPS'         0 8  72 19 4 100 $mutedHex
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis1 'FPS'       '%Framerate%' 0 26 72 36 4 200 $valueHex

# ── Level 2: Bar (452x72) ─────────────────────────────────────────────────────────────────────────
#    [CPU Ring] CPU / Power  |  FPS + 1% / Temp  |  GPU / Power [GPU Ring]
$vis2 = 'Tsume level 2 visible'
Add-SpriteLayer $mergedLayers $usedRelX $usedRelY $vis2 'Panel' 0 0 $sleekPanelX $sleekPanelY $sleekPanelW $sleekPanelH $totalWidth $totalHeight
Add-RingLayer   $mergedLayers $usedRelX $usedRelY $vis2 'CPU ring' $sleekCpuRing 11 5 $totalWidth $totalHeight
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis2 'CPU usage'    '%CPU usage%'           11  26 62 20 4 100 $valueHex
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis2 'CPU label'    'CPU'                   75  15 66 19 4 100 $cpuHex
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis2 'CPU power'    '%CPU power% W'         75  37 66 19 4 100 $valueHex

Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis2 'FPS label'    'FPS'                   157 13 0  0  0 100 $fpsHex
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis2 'FPS'          '%Framerate%'           156 24 0  0  0 200 $valueHex
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis2 '1% low label' '1%'                    220 17 0  0  0 100 $mutedHex
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis2 '1% low'       '%Framerate 1 Low%'     220 17 72 19 2 100 $valueHex
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis2 'Temp label'   'Temp'                  220 39 0  0  0 100 $mutedHex
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis2 'Temperature'  "%CPU temperature%$deg" 220 39 77 19 2 100 $tempColor

Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis2 'GPU label'    'GPU'                   311 15 66 19 4 100 $gpuHex
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis2 'GPU power'    '%GPU1 power% W'        311 37 66 19 4 100 $valueHex
Add-RingLayer   $mergedLayers $usedRelX $usedRelY $vis2 'GPU ring' $sleekGpuRing 379 5 $totalWidth $totalHeight
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis2 'GPU usage'    '%GPU1 usage%'          379 26 62 20 4 100 $valueHex

# ── Level 3: Detail (686x93) ──────────────────────────────────────────────────────────────────────
#    [CPU Ring] CPU + Power/Clock/RAM | [GPU Ring] GPU + Power/Clock/VRAM | FPS/Temp + lows + graph
$vis3 = 'Tsume level 3 visible'
Add-SpriteLayer $mergedLayers $usedRelX $usedRelY $vis3 'Panel' 0 0 $compactPanelX $compactPanelY $compactPanelW $compactPanelH $totalWidth $totalHeight

Add-RingLayer   $mergedLayers $usedRelX $usedRelY $vis3 'CPU ring' $compactCpuRing 12 13 $totalWidth $totalHeight
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis3 'CPU usage'       '%CPU usage%'            12  36 67  20 4 100 $valueHex
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis3 'CPU label'       'CPU'                    91  8  0   0  0 100 $cpuHex
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis3 'CPU Power label' 'Power'                  91  27 0   0  0 100 $mutedHex
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis3 'CPU Power'       '%CPU power% W'          91  27 127 19 2 100 $valueHex
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis3 'CPU Clock label' 'Clock'                  91  46 0   0  0 100 $mutedHex
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis3 'CPU Clock'       '%CPU clock% GHz'        91  46 127 19 2 100 $valueHex
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis3 'CPU RAM label'   'RAM'                    91  65 0   0  0 100 $mutedHex
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis3 'CPU RAM'         '%RAM usage% GB'         91  65 127 19 2 100 $valueHex

Add-RingLayer   $mergedLayers $usedRelX $usedRelY $vis3 'GPU ring' $compactGpuRing 246 13 $totalWidth $totalHeight
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis3 'GPU usage'       '%GPU1 usage%'           246 36 67  20 4 100 $valueHex
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis3 'GPU label'       'GPU'                    325 8  0   0  0 100 $gpuHex
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis3 'GPU Power label' 'Power'                  325 27 0   0  0 100 $mutedHex
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis3 'GPU Power'       '%GPU1 power% W'         325 27 127 19 2 100 $valueHex
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis3 'GPU Clock label' 'Clock'                  325 46 0   0  0 100 $mutedHex
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis3 'GPU Clock'       '%GPU clock% GHz'        325 46 127 19 2 100 $valueHex
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis3 'GPU VRAM label'  'VRAM'                   325 65 0   0  0 100 $mutedHex
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis3 'GPU VRAM'        '%GPU1 memory usage% GB' 325 65 127 19 2 100 $valueHex

Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis3 'FPS label'       'FPS'                    478 8  0   0  0 100 $fpsHex
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis3 'FPS'             '%Framerate%'            477 18 0   0  0 200 $valueHex
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis3 'Temp label'      'Temp'                   478 50 0   0  0 100 $mutedHex
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis3 'Temperature'     "%CPU temperature%$deg"  521 50 0   0  0 100 $tempColor
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis3 '1% label'        '1%'                     563 8  0   0  0 100 $mutedHex
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis3 '1%'              '%Framerate 1 Low%'      592 8  80  19 2 100 $valueHex
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis3 '0.1% label'      '0.1%'                   563 29 0   0  0 100 $mutedHex
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis3 '0.1%'            '%Framerate 01 Low%'     592 29 80  19 2 100 $valueHex
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis3 'FT label'        'FT'                     563 50 0   0  0 100 $mutedHex
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis3 'FT'              '<FT> ms'                592 50 80  19 2 100 $valueHex
Add-GraphLayer  $mergedLayers $usedRelX $usedRelY $vis3 'Frametime graph' '<G=Frametime,0,0,1,0,40,0>' 478 73 194 12 $fpsHex

# ── Level 4: Full side panel (225x504) ────────────────────────────────────────────────────────────
#    Header: FPS, Temp, 1% low, 0.1% low, Frametime, graph
#    CPU: Load, Power, Clock, RAM, graph
#    GPU: Load, Power, Clock, Voltage, Fan, VRAM, graph
$vis4 = 'Tsume level 4 visible'
Add-SpriteLayer $mergedLayers $usedRelX $usedRelY $vis4 'Panel' 0 0 $perfPanelX $perfPanelY $perfPanelW $perfPanelH $totalWidth $totalHeight

Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis4 'FPS label'         'FPS'                   16  11  0  0  0 100 $fpsHex
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis4 'FPS'               '%Framerate%'           15  21  0  0  0 200 $valueHex
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis4 'Temp label'        'Temp'                  16  56  0  0  0 100 $mutedHex
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis4 'Temperature'       "%CPU temperature%$deg" 115 56  98 19 2 100 $tempColor
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis4 '1% low label'      '1% low'                16  76  0  0  0 100 $mutedHex
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis4 '1% low'            '%Framerate 1 Low%'     118 76  90 19 2 100 $valueHex
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis4 '0.1% low label'    '0.1% low'              16  96  0  0  0 100 $mutedHex
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis4 '0.1% low'          '%Framerate 01 Low%'    118 96  90 19 2 100 $valueHex
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis4 'Frametime label'   'Frametime'             16  116 0  0  0 100 $mutedHex
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis4 'Frametime'         '<FT> ms'               118 116 90 19 2 100 $valueHex
Add-GraphLayer  $mergedLayers $usedRelX $usedRelY $vis4 'Frametime graph'   '<G=Frametime,0,0,1,0,40,10>' 16 139 192 19 $fpsHex

Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis4 'CPU label'         'CPU'                   16  178 0  0  0 100 $cpuHex
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis4 'CPU Load label'    'Load'                  16  199 0  0  0 100 $mutedHex
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis4 'CPU Load'          '%CPU usage%%'          110 199 98 19 2 100 $valueHex
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis4 'CPU Power label'   'Power'                 16  219 0  0  0 100 $mutedHex
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis4 'CPU Power'         '%CPU power% W'         110 219 98 19 2 100 $valueHex
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis4 'CPU Clock label'   'Clock'                 16  239 0  0  0 100 $mutedHex
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis4 'CPU Clock'         '%CPU clock% GHz'       110 239 98 19 2 100 $valueHex
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis4 'CPU RAM label'     'RAM'                   16  259 0  0  0 100 $mutedHex
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis4 'CPU RAM'           '%RAM usage% GB'        110 259 98 19 2 100 $valueHex
Add-GraphLayer  $mergedLayers $usedRelX $usedRelY $vis4 'CPU usage graph'   '<G=CPU usage,0,0,1,0,100,10>' 16 281 192 16 $cpuHex

Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis4 'GPU label'         'GPU'                   16  316 0  0  0 100 $gpuHex
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis4 'GPU Load label'    'Load'                  16  337 0  0  0 100 $mutedHex
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis4 'GPU Load'          '%GPU1 usage%%'         110 337 98 19 2 100 $valueHex
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis4 'GPU Power label'   'Power'                 16  357 0  0  0 100 $mutedHex
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis4 'GPU Power'         '%GPU1 power% W'        110 357 98 19 2 100 $valueHex
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis4 'GPU Clock label'   'Clock'                 16  377 0  0  0 100 $mutedHex
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis4 'GPU Clock'         '%GPU clock% GHz'       110 377 98 19 2 100 $valueHex
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis4 'GPU Voltage label' 'Voltage'               16  397 0  0  0 100 $mutedHex
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis4 'GPU Voltage'       '%GPU core voltage% V'  110 397 98 19 2 100 $valueHex
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis4 'GPU Fan label'     'Fan'                   16  417 0  0  0 100 $mutedHex
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis4 'GPU Fan'           '%GPU fan% rpm'         110 417 98 19 2 100 $valueHex
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis4 'GPU VRAM label'    'VRAM'                  16  437 0  0  0 100 $mutedHex
Add-TextLayer   $mergedLayers $usedRelX $usedRelY $vis4 'GPU VRAM'          '%GPU1 memory usage% GB' 110 437 98 19 2 100 $valueHex
Add-GraphLayer  $mergedLayers $usedRelX $usedRelY $vis4 'GPU usage graph'   '<G=GPU1 usage,0,0,1,0,100,10>' 16 460 192 16 $gpuHex

# Dynamic position offset sources backed by TSUME_POS_X / TSUME_POS_Y in AIDA64_SensorValues.
foreach ($dx in ($usedRelX | Sort-Object)) {
    $formula = if ($dx -eq 0) { 'validate(x) ? x : 24' } else { "(validate(x) ? x : 24) + $dx" }
    $sources.Add((New-SourceLines "PX_$dx" '' '%.0f' $formula 'AIDA' 'TSUME_POS_X'))
}
foreach ($dy in ($usedRelY | Sort-Object)) {
    $formula = if ($dy -eq 0) { 'validate(x) ? x : 24' } else { "(validate(x) ? x : 24) + $dy" }
    $sources.Add((New-SourceLines "PY_$dy" '' '%.0f' $formula 'AIDA' 'TSUME_POS_Y'))
}

if ($mergedLayers.Count -gt $maxLayers) {
    throw "Overlay layer count $($mergedLayers.Count) exceeds MAX_LAYERS ($maxLayers)"
}

# 4. Assemble and write the combined .ovl file (Windows-1252, CRLF).
$ovl = [System.Collections.Generic.List[string]]::new()
$ovl.Add('[Master]')
$ovl.Add('Implementation=2')
$ovl.Add("FontFace=$masterFontFace")
$ovl.Add("FontHeight=$masterFontHeight")
$ovl.Add("FontWeight=$masterFontWeight")
$ovl.Add("ZoomRatio=$masterZoomRatio")

$ovl.Add('[Settings]')
$ovl.Add('Name=Tsume Essential')
$ovl.Add('EnvVars=')
$ovl.Add("RefreshPeriod=$baseRefresh")
$ovl.Add('LockUserSettings=1')
$ovl.Add("EmbeddedImage=$outPngName")
$ovl.Add('PingAddr=127.0.0.1')

$ovl.Add('[General]')
$ovl.Add("Sources=$($sources.Count)")
$ovl.Add('Tables=0')
$ovl.Add("Layers=$($mergedLayers.Count)")

for ($i = 0; $i -lt $sources.Count; $i++) {
    $ovl.Add("[Source$i]")
    foreach ($l in $sources[$i]) { $ovl.Add($l) }
}

for ($i = 0; $i -lt $mergedLayers.Count; $i++) {
    $ovl.Add("[Layer$i]")
    foreach ($l in $mergedLayers[$i]) { $ovl.Add($l) }
}

$content = ($ovl -join "`r`n") + "`r`n"
[System.IO.File]::WriteAllText($outOvlPath, $content, $enc)

Write-Host ("Dark -> {0} ({1} sources, {2}/{3} layers) + {4} ({5}x{6})" -f `
    (Split-Path -Leaf $outOvlPath), $sources.Count, $mergedLayers.Count, $maxLayers, $outPngName, $totalWidth, $totalHeight)
