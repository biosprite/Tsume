<#
.SYNOPSIS
    Merges realHfde's Essential layouts (FPS, Sleek, Sleek Compact, Performance) into one
    combined OverlayEditor layout (essential_tsume*.ovl) and one stitched PNG atlas
    (essential_tsume*_atlas.png) per theme, reworked for single-chip handheld PCs with one
    shared temperature reading.

.DESCRIPTION
    Each level's layers are gated with VisibilitySource backed by AIDA64_SensorValues
    (TSUME_OVERLAY_LEVEL with Formula "x == N"), so Tsume switches levels and hides the
    overlay live without restarting RTSS. When Tsume is closed (mapping missing), every
    visibility source invalidates to FLT_MAX and nothing is drawn.

    Single-chip handheld PCs (APU/SoC) share one die between CPU and GPU, so every
    telemetry layout displays one unified temperature reading instead of duplicating
    CPU/GPU temperatures (and omits discrete GDDR VRAM temperature):
      - Level 1 (FPS):    FPS + 1% low pill
      - Level 2 (Bar):    Symmetrical CPU (ring + W) | FPS, 1% LOW, TEMP | GPU (W + ring)
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
$enc = [System.Text.Encoding]::GetEncoding(1252)
$maxLayers = 256
$deg = [char]0xB0
$degC = "${deg}C"

$themes = @(
    @{
        Folder     = 'Dark'
        Suffix     = ''
        CpuColor   = [System.Drawing.Color]::FromArgb(255, 10, 132, 255)
        GpuColor   = [System.Drawing.Color]::FromArgb(255, 191, 90, 242)
        ValueHex   = 'FFFFFF'
        MutedHex   = 'D1D1D6'
        CpuHex     = '0A84FF'
        GpuHex     = 'BF5AF2'
        FpsHex     = '30D158'
        TempColor  = 'FFFFFF,,1,FF9F0A,1,2,FF453A,2,(Temperature tier,1)'
        HasShadows = $false
    }
    @{
        Folder     = 'Smoke'
        Suffix     = '_smoke'
        CpuColor   = [System.Drawing.Color]::FromArgb(255, 10, 132, 255)
        GpuColor   = [System.Drawing.Color]::FromArgb(255, 191, 90, 242)
        ValueHex   = 'FFFFFF'
        MutedHex   = 'E5E5EA'
        CpuHex     = '0A84FF'
        GpuHex     = 'BF5AF2'
        FpsHex     = '30D158'
        TempColor  = 'FFFFFF,,1,FF9F0A,1,2,FF453A,2,(Temperature tier,1)'
        HasShadows = $false
    }
    @{
        Folder     = 'White'
        Suffix     = '_white'
        CpuColor   = [System.Drawing.Color]::FromArgb(255, 0,  122, 255)
        GpuColor   = [System.Drawing.Color]::FromArgb(255, 175, 82, 222)
        ValueHex   = '0A0A0C'
        MutedHex   = '3A3A3C'
        CpuHex     = '007AFF'
        GpuHex     = 'AF52DE'
        FpsHex     = '248A3D'
        TempColor  = '0A0A0C,,1,C93400,1,2,D70015,2,(Temperature tier,1)'
        HasShadows = $false
    }
    @{
        Folder     = 'Float'
        Suffix     = '_float'
        CpuColor   = [System.Drawing.Color]::FromArgb(255, 10, 132, 255)
        GpuColor   = [System.Drawing.Color]::FromArgb(255, 191, 90, 242)
        ValueHex   = 'FFFFFF'
        MutedHex   = 'E5E5EA'
        CpuHex     = '0A84FF'
        GpuHex     = 'BF5AF2'
        FpsHex     = '30D158'
        TempColor  = 'FFFFFF,,1,FF9F0A,1,2,FF453A,2,(Temperature tier,1)'
        HasShadows = $true
    }
)

# High-resolution ring sprites (145 frames = 2.5 deg / ~1.2 px per step; exact 0/25/50/75/100% frames)
# animated at ~60 FPS (RefreshPeriod=16) via a two-stage critically damped EMA on HAL/Timer.
$ringFrames  = 145
$ringPerLine = 29
$ringRows    = [int][Math]::Ceiling($ringFrames / [double]$ringPerLine)
$ringMax     = '100.6944'
$ringBias    = '0.5'
$ringAlpha   = '0.18'
$ringRefresh = 16
$baseRefresh = 250

# Reworked panel chrome dimensions.
$sleekPanelW   = 520; $sleekPanelH   = 72;  $sleekDividers   = @(144, 376)
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

    # Preserve exact left and right rounded caps from the theme's upstream atlas.
    $target.DrawImage($sourceAtlas,
        (New-Object System.Drawing.Rectangle($destX, $destY, $capW, $height)),
        (New-Object System.Drawing.Rectangle(0, 0, $capW, $height)),
        [System.Drawing.GraphicsUnit]::Pixel)

    # Tile a clean divider-free 1px column across the middle (all four themes are horizontally uniform).
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

    # Stamp the theme's exact vertical divider strip at each section boundary.
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

    # Build a clean divider-free copy of the upstream panel so Smoke's vertical gradient scales seamlessly.
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

        # Re-apply the theme's exact horizontal divider delta at each new divider Y.
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

    # With PixelOffsetMode.HighQuality, pixel (x,y) is centered at (x+0.5, y+0.5),
    # so the geometric center of an N-pixel sprite is N/2.0 (31.0 for 62px, 33.5 for 67px).
    $center = $size / 2.0
    $rect   = New-Object System.Drawing.RectangleF(($center - $radius), ($center - $radius), (2.0 * $radius), (2.0 * $radius))
    $srcRect = New-Object System.Drawing.Rectangle($trackSrcX, $trackSrcY, $size, $size)
    $dstRect = New-Object System.Drawing.Rectangle(0, 0, $size, $size)

    try {
        $cg = [System.Drawing.Graphics]::FromImage($cell)
        try {
            $cg.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
            $cg.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
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
    return @(
        "Name=$name"
        "Units=$units"
        "Format=$format"
        "Formula=$formula"
        "Provider=$provider"
        "ID=$id"
    )
}

function Add-SpriteLayer(
    [System.Collections.Generic.List[string[]]]$list,
    [string]$vis,
    [string]$name,
    [int]$px, [int]$py,
    [int]$sx, [int]$sy, [int]$sw, [int]$sh,
    [int]$atlasW, [int]$atlasH) {

    if ($sx + $sw -gt $atlasW -or $sy + $sh -gt $atlasH) {
        throw "Sprite '$name' out of stitched atlas bounds: x=$sx y=$sy w=$sw h=$sh vs ${atlasW}x${atlasH}"
    }
    $list.Add(@(
        "Name=$name"
        "VisibilitySource=$vis"
        "Text=<I=0,0,$sx,$sy,$sw,$sh>"
        "PositionX=$px"
        "PositionY=$py"
        "ExtentX=$sw"
        "ExtentY=$sh"
        "ExtentOrigin=0"
        "RefreshPeriod=$baseRefresh"
    ))
}

function Add-RingLayer(
    [System.Collections.Generic.List[string[]]]$list,
    [string]$vis,
    [string]$name,
    [hashtable]$ring,
    [int]$px, [int]$py,
    [int]$atlasW, [int]$atlasH) {

    $gridW = $ring.PerLine * $ring.Size
    $gridH = [int][Math]::Ceiling($ring.Frames / [double]$ring.PerLine) * $ring.Size
    if ($ring.X + $gridW -gt $atlasW -or $ring.Y + $gridH -gt $atlasH) {
        throw "Ring '$name' out of stitched atlas bounds: x=$($ring.X) y=$($ring.Y) ${gridW}x${gridH} vs ${atlasW}x${atlasH}"
    }
    $list.Add(@(
        "Name=$name"
        "VisibilitySource=$vis"
        "Text=<AI=$($ring.Source),0,0,0,$($ring.Max),$($ring.X),$($ring.Y),$($ring.Size),$($ring.Size),$($ring.Frames),$($ring.PerLine),$($ring.Bias)>"
        "PositionX=$px"
        "PositionY=$py"
        "ExtentX=$($ring.Size)"
        "ExtentY=$($ring.Size)"
        "ExtentOrigin=0"
        "RefreshPeriod=$ringRefresh"
    ))
}

function Add-GraphLayer(
    [System.Collections.Generic.List[string[]]]$list,
    [string]$vis,
    [string]$name,
    [string]$graphTag,
    [int]$px, [int]$py, [int]$ex, [int]$ey,
    [string]$color) {

    $list.Add(@(
        "Name=$name"
        "VisibilitySource=$vis"
        "Text=$graphTag"
        "PositionX=$px"
        "PositionY=$py"
        "ExtentX=$ex"
        "ExtentY=$ey"
        "ExtentOrigin=0"
        "TextColor=$color"
        "RefreshPeriod=$baseRefresh"
    ))
}

function Add-TextLayer(
    [System.Collections.Generic.List[string[]]]$list,
    [hashtable]$theme,
    [string]$vis,
    [string]$name,
    [string]$text,
    [int]$px, [int]$py, [int]$ex, [int]$ey, [int]$origin,
    [int]$size,
    [string]$color,
    [int]$refresh = $baseRefresh) {

    if ($theme.HasShadows) {
        foreach ($off in @(@(1, 1), @(0, 2))) {
            $shadowLines = [System.Collections.Generic.List[string]]::new()
            $shadowLines.Add("Name=$name shadow")
            $shadowLines.Add("VisibilitySource=$vis")
            $shadowLines.Add("Text=$text")
            $shadowLines.Add("PositionX=$($px + $off[0])")
            $shadowLines.Add("PositionY=$($py + $off[1])")
            $shadowLines.Add("ExtentX=$ex")
            $shadowLines.Add("ExtentY=$ey")
            $shadowLines.Add("ExtentOrigin=$origin")
            if ($size -ne 100) { $shadowLines.Add("Size=$size") }
            $shadowLines.Add('TextColor=000000')
            $shadowLines.Add("RefreshPeriod=$refresh")
            $list.Add($shadowLines.ToArray())
        }
    }

    $mainLines = [System.Collections.Generic.List[string]]::new()
    $mainLines.Add("Name=$name")
    $mainLines.Add("VisibilitySource=$vis")
    $mainLines.Add("Text=$text")
    $mainLines.Add("PositionX=$px")
    $mainLines.Add("PositionY=$py")
    $mainLines.Add("ExtentX=$ex")
    $mainLines.Add("ExtentY=$ey")
    $mainLines.Add("ExtentOrigin=$origin")
    if ($size -ne 100) { $mainLines.Add("Size=$size") }
    $mainLines.Add("TextColor=$color")
    $mainLines.Add("RefreshPeriod=$refresh")
    $list.Add($mainLines.ToArray())
}

foreach ($theme in $themes) {
    $dir = Join-Path $essentialRoot $theme.Folder
    $sfx = $theme.Suffix

    $sleekPngPath   = Join-Path $dir "essential_sleek${sfx}_atlas.png"
    $compactPngPath = Join-Path $dir "essential_sleek_compact${sfx}_atlas.png"
    $perfPngPath    = Join-Path $dir "essential_performance_overlay${sfx}_atlas.png"
    $outPngName     = "essential_tsume${sfx}_atlas.png"
    $outPngPath     = Join-Path $dir $outPngName
    $outOvlPath     = Join-Path $dir "essential_tsume${sfx}.ovl"

    # 1. Build the stitched atlas: the three resized single-temperature panel backgrounds +
    #    145-frame anti-aliased CPU/GPU rings for Bar (62x62) and Detail (67x67), packed inside 2048x2048.
    $imgSleek   = [System.Drawing.Bitmap]::FromFile($sleekPngPath)
    $imgCompact = [System.Drawing.Bitmap]::FromFile($compactPngPath)
    $imgPerf    = [System.Drawing.Bitmap]::FromFile($perfPngPath)

    try {
        $sleekRingSz   = 62
        $compactRingSz = 67

        $sleekPanelX   = 0;                 $sleekPanelY   = 0
        $compactPanelX = $sleekPanelW + 4;  $compactPanelY = 0

        $sleekCpuRingY   = $compactPanelH + 3
        $sleekGpuRingY   = $sleekCpuRingY   + ($ringRows * $sleekRingSz)   + 2
        $compactCpuRingY = $sleekGpuRingY   + ($ringRows * $sleekRingSz)   + 2
        $compactGpuRingY = $compactCpuRingY + ($ringRows * $compactRingSz) + 2
        $perfPanelX      = 0
        $perfPanelY      = $compactGpuRingY + ($ringRows * $compactRingSz) + 4

        $totalWidth  = [Math]::Max($compactPanelX + $compactPanelW, $ringPerLine * $compactRingSz)
        $totalHeight = $perfPanelY + $perfPanelH

        $stitched = New-Object System.Drawing.Bitmap($totalWidth, $totalHeight, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $g = [System.Drawing.Graphics]::FromImage($stitched)
            try {
                $g.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
                $g.Clear([System.Drawing.Color]::Transparent)

                # Synthesize resized panel chrome directly from each theme's upstream atlas.
                Draw-HorizontalPanel $g $imgSleek   480 $sleekPanelH   36 100 142 3 $sleekPanelX   $sleekPanelY   $sleekPanelW   $sleekDividers
                Draw-HorizontalPanel $g $imgCompact 750 $compactPanelH 24 100 267 3 $compactPanelX $compactPanelY $compactPanelW $compactDividers
                Draw-VerticalPerfPanel $g $imgPerf $perfPanelX $perfPanelY $perfPanelH $perfDividers

                # Render 145-frame rings on top of the exact original Frame-0 track sprite.
                Draw-RingGrid $g $imgSleek   0 74  $sleekRingSz   28.05 4.10 $theme.CpuColor 0 $sleekCpuRingY   $ringFrames $ringPerLine
                Draw-RingGrid $g $imgSleek   0 324 $sleekRingSz   28.05 4.10 $theme.GpuColor 0 $sleekGpuRingY   $ringFrames $ringPerLine
                Draw-RingGrid $g $imgCompact 0 95  $compactRingSz 30.49 4.35 $theme.CpuColor 0 $compactCpuRingY $ringFrames $ringPerLine
                Draw-RingGrid $g $imgCompact 0 365 $compactRingSz 30.49 4.35 $theme.GpuColor 0 $compactGpuRingY $ringFrames $ringPerLine
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

    $perfSections = Read-IniSections (Join-Path $dir "essential_performance_overlay${sfx}.ovl")
    $masterSection = $perfSections | Where-Object { $_.Name -eq 'Master' } | Select-Object -First 1

    # 2. Build combined sources:
    #    - 4 AIDA visibility sources (x == 1..4 on TSUME_OVERLAY_LEVEL)
    #    - 4 raw HAL sources (updated BEFORE AIDA in OverlayDataSourcesList::UpdateSources)
    #    - 4 effective AIDA sources that use HAL when valid, falling back to Tsume AIDA values
    #    - remaining HAL sources from essential_performance_overlay (excluding removed GPU/VRAM temps)
    #    - rolling 1% / 0.1% low from RTSS's 1024-frame Frametime ring buffer
    #    - two-stage critically damped EMA sources (HAL/Timer) for 60 FPS smooth ring animation
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
    # so they stay populated in-game without starting an RTSS benchmark recording.
    $sources.Add((New-SourceLines 'Framerate 1% Low'   'FPS' '%.0f' '1000 / percentile("Frametime", 99)'   'HAL' 'Stub'))
    $sources.Add((New-SourceLines 'Framerate 0.1% Low' 'FPS' '%.0f' '1000 / percentile("Frametime", 99.9)' 'HAL' 'Stub'))

    # Smooth 60 FPS resource ring sources (two-stage critically damped EMA on HAL/Timer).
    $sources.Add((New-SourceLines 'CPU usage stage1'  '%' '' "if(validate(`"CPU usage stage1`"), `"CPU usage stage1`" + (`"CPU usage`" - `"CPU usage stage1`") * $ringAlpha, `"CPU usage`")"          'HAL' 'Timer'))
    $sources.Add((New-SourceLines 'CPU usage smooth'  '%' '' "if(validate(`"CPU usage smooth`"), `"CPU usage smooth`" + (`"CPU usage stage1`" - `"CPU usage smooth`") * $ringAlpha, `"CPU usage`")"   'HAL' 'Timer'))
    $sources.Add((New-SourceLines 'GPU1 usage stage1' '%' '' "if(validate(`"GPU1 usage stage1`"), `"GPU1 usage stage1`" + (`"GPU1 usage`" - `"GPU1 usage stage1`") * $ringAlpha, `"GPU1 usage`")"     'HAL' 'Timer'))
    $sources.Add((New-SourceLines 'GPU1 usage smooth' '%' '' "if(validate(`"GPU1 usage smooth`"), `"GPU1 usage smooth`" + (`"GPU1 usage stage1`" - `"GPU1 usage smooth`") * $ringAlpha, `"GPU1 usage`")" 'HAL' 'Timer'))

    # 3. Build layers for all four levels.
    $mergedLayers = [System.Collections.Generic.List[string[]]]::new()

    # ── Level 1: FPS pill (reuses Bar panel caps + clean center fill) ─────────────────────────────
    $vis1 = 'Tsume level 1 visible'
    Add-SpriteLayer $mergedLayers $vis1 'Panel left'   834 24 ($sleekPanelX + 0)                    $sleekPanelY 36  72 $totalWidth $totalHeight
    Add-SpriteLayer $mergedLayers $vis1 'Panel middle' 870 24 ($sleekPanelX + 160)                  $sleekPanelY 180 72 $totalWidth $totalHeight
    Add-SpriteLayer $mergedLayers $vis1 'Panel right'  1050 24 ($sleekPanelX + $sleekPanelW - 36)   $sleekPanelY 36  72 $totalWidth $totalHeight
    Add-TextLayer   $mergedLayers $theme $vis1 'FPS label'    'FPS'                870 32 90 19 4 100 $theme.MutedHex
    Add-TextLayer   $mergedLayers $theme $vis1 'FPS'          '%Framerate%'        870 50 90 36 4 200 $theme.ValueHex
    Add-TextLayer   $mergedLayers $theme $vis1 '1% low label' '1% LOW'             960 32 90 19 4 100 $theme.MutedHex
    Add-TextLayer   $mergedLayers $theme $vis1 '1% low'       '%Framerate 1% Low%' 960 50 90 36 4 200 $theme.ValueHex 1000

    # ── Level 2: Bar (520x72, centered at x=960) ──────────────────────────────────────────────────
    #    [CPU Ring] CPU / Power  |  FPS  1% LOW  TEMP  |  GPU / Power [GPU Ring]
    $vis2 = 'Tsume level 2 visible'
    Add-SpriteLayer $mergedLayers $vis2 'Panel' 700 24 $sleekPanelX $sleekPanelY $sleekPanelW $sleekPanelH $totalWidth $totalHeight
    Add-RingLayer   $mergedLayers $vis2 'CPU ring' $sleekCpuRing 711 29 $totalWidth $totalHeight
    Add-TextLayer   $mergedLayers $theme $vis2 'CPU usage'    '%CPU usage%'           711  50 62 20 4 100 $theme.ValueHex
    Add-TextLayer   $mergedLayers $theme $vis2 'CPU label'    'CPU'                   775  39 66 19 4 100 $theme.CpuHex
    Add-TextLayer   $mergedLayers $theme $vis2 'CPU power'    '%CPU power% W'         775  61 66 19 4 100 $theme.ValueHex

    Add-TextLayer   $mergedLayers $theme $vis2 'FPS label'    'FPS'                   846  32 76 19 4 100 $theme.MutedHex
    Add-TextLayer   $mergedLayers $theme $vis2 'FPS'          '%Framerate%'           846  50 76 36 4 200 $theme.ValueHex
    Add-TextLayer   $mergedLayers $theme $vis2 '1% low label' '1% LOW'                922  32 76 19 4 100 $theme.MutedHex
    Add-TextLayer   $mergedLayers $theme $vis2 '1% low'       '%Framerate 1% Low%'    922  50 76 36 4 200 $theme.ValueHex 1000
    Add-TextLayer   $mergedLayers $theme $vis2 'Temp label'   'TEMP'                  998  32 76 19 4 100 $theme.MutedHex
    Add-TextLayer   $mergedLayers $theme $vis2 'Temperature'  "%CPU temperature%$deg" 1001 50 76 36 4 200 $theme.TempColor

    Add-TextLayer   $mergedLayers $theme $vis2 'GPU label'    'GPU'                   1079 39 66 19 4 100 $theme.GpuHex
    Add-TextLayer   $mergedLayers $theme $vis2 'GPU power'    '%GPU1 power% W'        1079 61 66 19 4 100 $theme.ValueHex
    Add-RingLayer   $mergedLayers $vis2 'GPU ring' $sleekGpuRing 1147 29 $totalWidth $totalHeight
    Add-TextLayer   $mergedLayers $theme $vis2 'GPU usage'    '%GPU1 usage%'          1147 50 62 20 4 100 $theme.ValueHex

    # ── Level 3: Detail (686x93, centered at x=960) ───────────────────────────────────────────────
    #    [CPU Ring] CPU + Power/Clock/RAM | [GPU Ring] GPU + Power/Clock/VRAM | FPS/Temp + lows + graph
    $vis3 = 'Tsume level 3 visible'
    Add-SpriteLayer $mergedLayers $vis3 'Panel' 617 24 $compactPanelX $compactPanelY $compactPanelW $compactPanelH $totalWidth $totalHeight

    Add-RingLayer   $mergedLayers $vis3 'CPU ring' $compactCpuRing 629 37 $totalWidth $totalHeight
    Add-TextLayer   $mergedLayers $theme $vis3 'CPU usage'       '%CPU usage%'         629 60 67  20 4 100 $theme.ValueHex
    Add-TextLayer   $mergedLayers $theme $vis3 'CPU label'       'CPU'                 708 32 0   0  0 100 $theme.CpuHex
    Add-TextLayer   $mergedLayers $theme $vis3 'CPU Power label' 'Power'               708 51 0   0  0 100 $theme.MutedHex
    Add-TextLayer   $mergedLayers $theme $vis3 'CPU Power'       '%CPU power% W'       708 51 127 19 2 100 $theme.ValueHex
    Add-TextLayer   $mergedLayers $theme $vis3 'CPU Clock label' 'Clock'               708 70 0   0  0 100 $theme.MutedHex
    Add-TextLayer   $mergedLayers $theme $vis3 'CPU Clock'       '%CPU clock% GHz'     708 70 127 19 2 100 $theme.ValueHex
    Add-TextLayer   $mergedLayers $theme $vis3 'CPU RAM label'   'RAM'                 708 89 0   0  0 100 $theme.MutedHex
    Add-TextLayer   $mergedLayers $theme $vis3 'CPU RAM'         '%RAM usage% GB'      708 89 127 19 2 100 $theme.ValueHex

    Add-RingLayer   $mergedLayers $vis3 'GPU ring' $compactGpuRing 863 37 $totalWidth $totalHeight
    Add-TextLayer   $mergedLayers $theme $vis3 'GPU usage'       '%GPU1 usage%'        863 60 67  20 4 100 $theme.ValueHex
    Add-TextLayer   $mergedLayers $theme $vis3 'GPU label'       'GPU'                 942 32 0   0  0 100 $theme.GpuHex
    Add-TextLayer   $mergedLayers $theme $vis3 'GPU Power label' 'Power'               942 51 0   0  0 100 $theme.MutedHex
    Add-TextLayer   $mergedLayers $theme $vis3 'GPU Power'       '%GPU1 power% W'      942 51 127 19 2 100 $theme.ValueHex
    Add-TextLayer   $mergedLayers $theme $vis3 'GPU Clock label' 'Clock'               942 70 0   0  0 100 $theme.MutedHex
    Add-TextLayer   $mergedLayers $theme $vis3 'GPU Clock'       '%GPU clock% GHz'     942 70 127 19 2 100 $theme.ValueHex
    Add-TextLayer   $mergedLayers $theme $vis3 'GPU VRAM label'  'VRAM'                942 89 0   0  0 100 $theme.MutedHex
    Add-TextLayer   $mergedLayers $theme $vis3 'GPU VRAM'        '%GPU1 memory usage% GB' 942 89 127 19 2 100 $theme.ValueHex

    Add-TextLayer   $mergedLayers $theme $vis3 'FPS label'       'FPS'                  1095 32 0   0  0 100 $theme.FpsHex
    Add-TextLayer   $mergedLayers $theme $vis3 'FPS'             '%Framerate%'          1094 42 0   0  0 200 $theme.ValueHex
    Add-TextLayer   $mergedLayers $theme $vis3 'Temp label'      'Temp'                 1095 74 0   0  0 100 $theme.MutedHex
    Add-TextLayer   $mergedLayers $theme $vis3 'Temperature'     "%CPU temperature%$deg" 1138 74 0  0  0 100 $theme.TempColor
    Add-TextLayer   $mergedLayers $theme $vis3 '1% label'        '1%'                   1180 32 0   0  0 100 $theme.MutedHex
    Add-TextLayer   $mergedLayers $theme $vis3 '1%'              '%Framerate 1% Low%'   1209 32 80  19 2 100 $theme.ValueHex 1000
    Add-TextLayer   $mergedLayers $theme $vis3 '0.1% label'      '0.1%'                 1180 53 0   0  0 100 $theme.MutedHex
    Add-TextLayer   $mergedLayers $theme $vis3 '0.1%'            '%Framerate 0.1% Low%' 1209 53 80  19 2 100 $theme.ValueHex 1000
    Add-TextLayer   $mergedLayers $theme $vis3 'FT label'        'FT'                   1180 74 0   0  0 100 $theme.MutedHex
    Add-TextLayer   $mergedLayers $theme $vis3 'FT'              '<FT> ms'              1209 74 80  19 2 100 $theme.ValueHex
    Add-GraphLayer  $mergedLayers $vis3 'Frametime graph' '<G=Frametime,0,0,1,0,40,0>'  1095 97 194 12 $theme.FpsHex

    # ── Level 4: Full side panel (225x504, top-right at x=1671, y=160) ────────────────────────────
    #    Header: FPS (left) + TEMP (right), 1% low, 0.1% low, Frametime, graph
    #    CPU: Load, Power, Clock, RAM, graph
    #    GPU: Load, Power, Clock, Voltage, Fan, VRAM, graph
    $vis4 = 'Tsume level 4 visible'
    Add-SpriteLayer $mergedLayers $vis4 'Panel' 1671 160 $perfPanelX $perfPanelY $perfPanelW $perfPanelH $totalWidth $totalHeight

    Add-TextLayer   $mergedLayers $theme $vis4 'FPS label'        'FPS'                  1687 173 0  0  0 100 $theme.FpsHex
    Add-TextLayer   $mergedLayers $theme $vis4 'FPS'              '%Framerate%'          1687 188 0  0  0 200 $theme.ValueHex
    Add-TextLayer   $mergedLayers $theme $vis4 'Temp label'       'TEMP'                 1781 173 98 19 2 100 $theme.MutedHex
    Add-TextLayer   $mergedLayers $theme $vis4 'Temperature'      "%CPU temperature%$deg" 1781 188 98 38 2 200 $theme.TempColor
    Add-TextLayer   $mergedLayers $theme $vis4 '1% low label'     '1% low'               1687 234 0  0  0 100 $theme.MutedHex
    Add-TextLayer   $mergedLayers $theme $vis4 '1% low'           '%Framerate 1% Low%'   1789 234 90 19 2 100 $theme.ValueHex 1000
    Add-TextLayer   $mergedLayers $theme $vis4 '0.1% low label'   '0.1% low'             1687 254 0  0  0 100 $theme.MutedHex
    Add-TextLayer   $mergedLayers $theme $vis4 '0.1% low'         '%Framerate 0.1% Low%' 1789 254 90 19 2 100 $theme.ValueHex 1000
    Add-TextLayer   $mergedLayers $theme $vis4 'Frametime label'  'Frametime'            1687 274 0  0  0 100 $theme.MutedHex
    Add-TextLayer   $mergedLayers $theme $vis4 'Frametime'        '<FT> ms'              1789 274 90 19 2 100 $theme.ValueHex
    Add-GraphLayer  $mergedLayers $vis4 'Frametime graph' '<G=Frametime,0,0,1,0,40,10>'  1687 298 192 20 $theme.FpsHex

    Add-TextLayer   $mergedLayers $theme $vis4 'CPU label'        'CPU'                  1687 338 0  0  0 100 $theme.CpuHex
    Add-TextLayer   $mergedLayers $theme $vis4 'CPU Load label'   'Load'                 1687 359 0  0  0 100 $theme.MutedHex
    Add-TextLayer   $mergedLayers $theme $vis4 'CPU Load'         '%CPU usage%%'         1781 359 98 19 2 100 $theme.ValueHex
    Add-TextLayer   $mergedLayers $theme $vis4 'CPU Power label'  'Power'                1687 379 0  0  0 100 $theme.MutedHex
    Add-TextLayer   $mergedLayers $theme $vis4 'CPU Power'        '%CPU power% W'        1781 379 98 19 2 100 $theme.ValueHex
    Add-TextLayer   $mergedLayers $theme $vis4 'CPU Clock label'  'Clock'                1687 399 0  0  0 100 $theme.MutedHex
    Add-TextLayer   $mergedLayers $theme $vis4 'CPU Clock'        '%CPU clock% GHz'      1781 399 98 19 2 100 $theme.ValueHex
    Add-TextLayer   $mergedLayers $theme $vis4 'CPU RAM label'    'RAM'                  1687 419 0  0  0 100 $theme.MutedHex
    Add-TextLayer   $mergedLayers $theme $vis4 'CPU RAM'          '%RAM usage% GB'       1781 419 98 19 2 100 $theme.ValueHex
    Add-GraphLayer  $mergedLayers $vis4 'CPU usage graph' '<G=CPU usage,0,0,1,0,100,10>' 1687 441 192 16 $theme.CpuHex

    Add-TextLayer   $mergedLayers $theme $vis4 'GPU label'        'GPU'                  1687 476 0  0  0 100 $theme.GpuHex
    Add-TextLayer   $mergedLayers $theme $vis4 'GPU Load label'   'Load'                 1687 497 0  0  0 100 $theme.MutedHex
    Add-TextLayer   $mergedLayers $theme $vis4 'GPU Load'         '%GPU1 usage%%'        1781 497 98 19 2 100 $theme.ValueHex
    Add-TextLayer   $mergedLayers $theme $vis4 'GPU Power label'  'Power'                1687 517 0  0  0 100 $theme.MutedHex
    Add-TextLayer   $mergedLayers $theme $vis4 'GPU Power'        '%GPU1 power% W'       1781 517 98 19 2 100 $theme.ValueHex
    Add-TextLayer   $mergedLayers $theme $vis4 'GPU Clock label'  'Clock'                1687 537 0  0  0 100 $theme.MutedHex
    Add-TextLayer   $mergedLayers $theme $vis4 'GPU Clock'        '%GPU clock% GHz'      1781 537 98 19 2 100 $theme.ValueHex
    Add-TextLayer   $mergedLayers $theme $vis4 'GPU Voltage label' 'Voltage'             1687 557 0  0  0 100 $theme.MutedHex
    Add-TextLayer   $mergedLayers $theme $vis4 'GPU Voltage'      '%GPU core voltage% V' 1781 557 98 19 2 100 $theme.ValueHex
    Add-TextLayer   $mergedLayers $theme $vis4 'GPU Fan label'    'Fan'                  1687 577 0  0  0 100 $theme.MutedHex
    Add-TextLayer   $mergedLayers $theme $vis4 'GPU Fan'          '%GPU fan% rpm'        1781 577 98 19 2 100 $theme.ValueHex
    Add-TextLayer   $mergedLayers $theme $vis4 'GPU VRAM label'   'VRAM'                 1687 597 0  0  0 100 $theme.MutedHex
    Add-TextLayer   $mergedLayers $theme $vis4 'GPU VRAM'         '%GPU1 memory usage% GB' 1781 597 98 19 2 100 $theme.ValueHex
    Add-GraphLayer  $mergedLayers $vis4 'GPU usage graph' '<G=GPU1 usage,0,0,1,0,100,10>' 1687 620 192 16 $theme.GpuHex

    if ($mergedLayers.Count -gt $maxLayers) {
        throw "$($theme.Folder): merged layer count $($mergedLayers.Count) exceeds MAX_LAYERS ($maxLayers)"
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
    $ovl.Add("Name=Tsume Essential ($($theme.Folder))")
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

    Write-Host ("{0,-6} -> {1} ({2} sources, {3}/{4} layers) + {5} ({6}x{7})" -f `
        $theme.Folder, (Split-Path -Leaf $outOvlPath), $sources.Count, $mergedLayers.Count, $maxLayers, $outPngName, $totalWidth, $totalHeight)
}
