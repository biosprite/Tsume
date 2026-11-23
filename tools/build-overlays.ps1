<#
.SYNOPSIS
    Merges realHfde's Essential layouts (FPS, Sleek, Sleek Compact, Performance) into one
    combined OverlayEditor layout (essential_tsume*.ovl) and one stitched PNG atlas
    (essential_tsume*_atlas.png) per theme.

.DESCRIPTION
    Each level's layers are gated with VisibilitySource backed by AIDA64_SensorValues
    (TSUME_OVERLAY_LEVEL with Formula "x == N"), so Tsume switches levels and hides the
    overlay live without restarting RTSS. When Tsume is closed (mapping missing), every
    visibility source invalidates to FLT_MAX and nothing is drawn.

    CPU temperature, CPU power, GPU temperature, and GPU power prefer OverlayEditor's
    built-in HAL source when valid and fall back to Tsume's AIDA64 readings (EC + PDH
    Energy Meter) when HAL reports blank values.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repoRoot = Split-Path -Parent $PSScriptRoot
$essentialRoot = Join-Path $repoRoot 'src\Tsume.App\Overlays\Essential'
$enc = [System.Text.Encoding]::GetEncoding(1252)
$maxLayers = 256

$themes = @(
    @{ Folder = 'Dark';  Suffix = '';       CpuColor = [System.Drawing.Color]::FromArgb(255, 10, 132, 255); GpuColor = [System.Drawing.Color]::FromArgb(255, 191, 90, 242) }
    @{ Folder = 'Smoke'; Suffix = '_smoke'; CpuColor = [System.Drawing.Color]::FromArgb(255, 10, 132, 255); GpuColor = [System.Drawing.Color]::FromArgb(255, 191, 90, 242) }
    @{ Folder = 'White'; Suffix = '_white'; CpuColor = [System.Drawing.Color]::FromArgb(255, 0,  122, 255); GpuColor = [System.Drawing.Color]::FromArgb(255, 175, 82, 222) }
    @{ Folder = 'Float'; Suffix = '_float'; CpuColor = [System.Drawing.Color]::FromArgb(255, 10, 132, 255); GpuColor = [System.Drawing.Color]::FromArgb(255, 191, 90, 242) }
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

function Rewrite-SpriteTags(
    [string]$text,
    [int]$offsetX,
    [int]$offsetY,
    [hashtable]$ringMap,
    [int]$atlasWidth,
    [int]$atlasHeight) {

    # <I=w,h,sx,sy,sw,sh[,rot]>  -> indices 2,3 are dwSpriteX, dwSpriteY
    $text = [regex]::Replace($text, '<I=([^>]+)>', {
        param($m)
        $parts = $m.Groups[1].Value -split ','
        if ($parts.Length -lt 6) { return $m.Value }
        $sx = [int]$parts[2] + $offsetX
        $sy = [int]$parts[3] + $offsetY
        $sw = [int]$parts[4]
        $sh = [int]$parts[5]
        if ($sx + $sw -gt $atlasWidth -or $sy + $sh -gt $atlasHeight) {
            throw "Sprite <I> out of stitched atlas bounds: x=$sx y=$sy w=$sw h=$sh vs ${atlasWidth}x${atlasHeight}"
        }
        $parts[2] = [string]$sx
        $parts[3] = [string]$sy
        return '<I=' + ($parts -join ',') + '>'
    })

    # <AI=source,w,h,min,max,sx,sy,sw,sh,num,perLine[,bias,rotMin,rotMax]>
    # Replace original 51-frame CPU/GPU rings with smooth 145-frame rings driven by the eased Timer sources.
    $text = [regex]::Replace($text, '<AI=([^>]+)>', {
        param($m)
        $parts = $m.Groups[1].Value -split ','
        if ($parts.Length -lt 11) { return $m.Value }
        $src = $parts[0]
        if ($null -ne $ringMap -and $ringMap.ContainsKey($src)) {
            $r = $ringMap[$src]
            $gridW = $r.PerLine * $r.Size
            $gridH = [int][Math]::Ceiling($r.Frames / [double]$r.PerLine) * $r.Size
            if ($r.X + $gridW -gt $atlasWidth -or $r.Y + $gridH -gt $atlasHeight) {
                throw "Ring <AI> out of stitched atlas bounds: x=$($r.X) y=$($r.Y) ${gridW}x${gridH} vs ${atlasWidth}x${atlasHeight}"
            }
            return "<AI=$($r.Source),$($parts[1]),$($parts[2]),0,$($r.Max),$($r.X),$($r.Y),$($r.Size),$($r.Size),$($r.Frames),$($r.PerLine),$($r.Bias)>"
        }
        $parts[5] = [string]([int]$parts[5] + $offsetX)
        $parts[6] = [string]([int]$parts[6] + $offsetY)
        return '<AI=' + ($parts -join ',') + '>'
    })

    return $text
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

foreach ($theme in $themes) {
    $dir = Join-Path $essentialRoot $theme.Folder
    $sfx = $theme.Suffix

    $sleekPngPath   = Join-Path $dir "essential_sleek${sfx}_atlas.png"
    $compactPngPath = Join-Path $dir "essential_sleek_compact${sfx}_atlas.png"
    $perfPngPath    = Join-Path $dir "essential_performance_overlay${sfx}_atlas.png"
    $outPngName     = "essential_tsume${sfx}_atlas.png"
    $outPngPath     = Join-Path $dir $outPngName
    $outOvlPath     = Join-Path $dir "essential_tsume${sfx}.ovl"

    # 1. Build the stitched atlas: the three panel backgrounds + 145-frame anti-aliased CPU/GPU rings
    #    for Sleek (62x62) and Sleek Compact (67x67), all packed cleanly inside 2048x2048.
    $imgSleek   = [System.Drawing.Bitmap]::FromFile($sleekPngPath)
    $imgCompact = [System.Drawing.Bitmap]::FromFile($compactPngPath)
    $imgPerf    = [System.Drawing.Bitmap]::FromFile($perfPngPath)

    try {
        $sleekPanelW   = 480; $sleekPanelH   = 72
        $compactPanelW = 750; $compactPanelH = 93
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
        $totalHeight = $perfPanelY + $imgPerf.Height

        $stitched = New-Object System.Drawing.Bitmap($totalWidth, $totalHeight, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $g = [System.Drawing.Graphics]::FromImage($stitched)
            try {
                $g.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
                $g.Clear([System.Drawing.Color]::Transparent)

                # Copy panel chrome untouched from the upstream Essential atlases.
                $g.DrawImage($imgSleek,
                    (New-Object System.Drawing.Rectangle($sleekPanelX, $sleekPanelY, $sleekPanelW, $sleekPanelH)),
                    (New-Object System.Drawing.Rectangle(0, 0, $sleekPanelW, $sleekPanelH)),
                    [System.Drawing.GraphicsUnit]::Pixel)
                $g.DrawImage($imgCompact,
                    (New-Object System.Drawing.Rectangle($compactPanelX, $compactPanelY, $compactPanelW, $compactPanelH)),
                    (New-Object System.Drawing.Rectangle(0, 0, $compactPanelW, $compactPanelH)),
                    [System.Drawing.GraphicsUnit]::Pixel)
                $g.DrawImageUnscaled($imgPerf, $perfPanelX, $perfPanelY)

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

    $sleekRings = @{
        'CPU usage'  = @{ Source = 'CPU usage smooth';  X = 0; Y = $sleekCpuRingY; Size = $sleekRingSz; Frames = $ringFrames; PerLine = $ringPerLine; Max = $ringMax; Bias = $ringBias }
        'GPU1 usage' = @{ Source = 'GPU1 usage smooth'; X = 0; Y = $sleekGpuRingY; Size = $sleekRingSz; Frames = $ringFrames; PerLine = $ringPerLine; Max = $ringMax; Bias = $ringBias }
    }
    $compactRings = @{
        'CPU usage'  = @{ Source = 'CPU usage smooth';  X = 0; Y = $compactCpuRingY; Size = $compactRingSz; Frames = $ringFrames; PerLine = $ringPerLine; Max = $ringMax; Bias = $ringBias }
        'GPU1 usage' = @{ Source = 'GPU1 usage smooth'; X = 0; Y = $compactGpuRingY; Size = $compactRingSz; Frames = $ringFrames; PerLine = $ringPerLine; Max = $ringMax; Bias = $ringBias }
    }

    # 2. Parse the four source layouts.
    $levelDefs = @(
        @{ Level = 1; Vis = 'Tsume level 1 visible'; Path = (Join-Path $dir "essential_fps${sfx}.ovl");                 OffsetX = $sleekPanelX;   OffsetY = $sleekPanelY;   Rings = $null         }
        @{ Level = 2; Vis = 'Tsume level 2 visible'; Path = (Join-Path $dir "essential_sleek${sfx}.ovl");               OffsetX = $sleekPanelX;   OffsetY = $sleekPanelY;   Rings = $sleekRings   }
        @{ Level = 3; Vis = 'Tsume level 3 visible'; Path = (Join-Path $dir "essential_sleek_compact${sfx}.ovl");       OffsetX = $compactPanelX; OffsetY = $compactPanelY; Rings = $compactRings }
        @{ Level = 4; Vis = 'Tsume level 4 visible'; Path = (Join-Path $dir "essential_performance_overlay${sfx}.ovl"); OffsetX = $perfPanelX;    OffsetY = $perfPanelY;    Rings = $null         }
    )

    $perfSections = Read-IniSections $levelDefs[3].Path
    $masterSection = $perfSections | Where-Object { $_.Name -eq 'Master' } | Select-Object -First 1

    # 3. Build combined sources:
    #    - 4 AIDA visibility sources (x == 1..4 on TSUME_OVERLAY_LEVEL)
    #    - 5 raw HAL sources (updated BEFORE AIDA in OverlayDataSourcesList::UpdateSources)
    #    - 5 effective AIDA sources that use HAL when valid, falling back to Tsume AIDA values
    #    - remaining HAL sources from essential_performance_overlay unchanged
    #    - rolling 1% / 0.1% low from RTSS's 1024-frame Frametime ring buffer
    #    - two-stage critically damped EMA sources (HAL/Timer) for 60 FPS smooth ring animation
    $degC = [char]0xB0 + 'C'
    $sources = [System.Collections.Generic.List[string[]]]::new()

    for ($lvl = 1; $lvl -le 4; $lvl++) {
        $sources.Add((New-SourceLines "Tsume level $lvl visible" '' '%.0f' "x == $lvl" 'AIDA' 'TSUME_OVERLAY_LEVEL'))
    }

    $fallbackSpecs = @(
        @{ Display = 'CPU temperature';  Raw = 'HAL CPU temperature';  Units = $degC; Format = '';     Scale = '';       HalId = 'CPU temperature';  AidaId = 'TSUME_CPU_TEMP'  }
        @{ Display = 'CPU power';        Raw = 'HAL CPU power';        Units = 'W';   Format = '';     Scale = '';       HalId = 'CPU power';        AidaId = 'TSUME_CPU_POWER' }
        @{ Display = 'CPU clock';        Raw = 'HAL CPU clock';        Units = 'GHz'; Format = '%.2f'; Scale = ' / 1000'; HalId = 'CPU clock';       AidaId = 'TSUME_CPU_CLOCK' }
        @{ Display = 'GPU1 temperature'; Raw = 'HAL GPU1 temperature'; Units = $degC; Format = '';     Scale = '';       HalId = 'GPU1 temperature'; AidaId = 'TSUME_GPU_TEMP'  }
        @{ Display = 'GPU1 power';       Raw = 'HAL GPU1 power';       Units = 'W';   Format = '';     Scale = '';       HalId = 'GPU1 power';       AidaId = 'TSUME_GPU_POWER' }
    )
    $replacedNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($spec in $fallbackSpecs) { [void]$replacedNames.Add($spec.Display) }

    foreach ($spec in $fallbackSpecs) {
        $sources.Add((New-SourceLines $spec.Raw $spec.Units '' '' 'HAL' $spec.HalId))
    }
    foreach ($spec in $fallbackSpecs) {
        $formula = "(validate(`"$($spec.Raw)`") ? `"$($spec.Raw)`" : x)$($spec.Scale)"
        $sources.Add((New-SourceLines $spec.Display $spec.Units $spec.Format $formula 'AIDA' $spec.AidaId))
    }

    foreach ($sec in ($perfSections | Where-Object { $_.Name -match '^Source\d+$' })) {
        $nameLine = $sec.Lines | Where-Object { $_ -match '^Name=' } | Select-Object -First 1
        $srcName = if ($nameLine) { $nameLine.Substring(5) } else { '' }
        if ($replacedNames.Contains($srcName)) { continue }
        $sources.Add($sec.Lines.ToArray())
    }

    # Live rolling 1% low and 0.1% low from RTSS's always-active 1024-frame Frametime ring buffer,
    # so they stay populated in-game without starting an RTSS benchmark recording.
    # CalcDiv in FormulaTranslator returns FLT_MAX (blank) when Frametime is 0 or FLT_MAX.
    $sources.Add((New-SourceLines 'Framerate 1% Low'   'FPS' '%.0f' '1000 / percentile("Frametime", 99)'   'HAL' 'Stub'))
    $sources.Add((New-SourceLines 'Framerate 0.1% Low' 'FPS' '%.0f' '1000 / percentile("Frametime", 99.9)' 'HAL' 'Stub'))

    # Smooth 60 FPS resource ring sources (two-stage critically damped EMA on HAL/Timer).
    # OverlayEditor invokes UpdateTimerDerivedSources() on every 16 ms ring layer tick without polling hardware.
    $sources.Add((New-SourceLines 'CPU usage stage1'  '%' '' "if(validate(`"CPU usage stage1`"), `"CPU usage stage1`" + (`"CPU usage`" - `"CPU usage stage1`") * $ringAlpha, `"CPU usage`")"          'HAL' 'Timer'))
    $sources.Add((New-SourceLines 'CPU usage smooth'  '%' '' "if(validate(`"CPU usage smooth`"), `"CPU usage smooth`" + (`"CPU usage stage1`" - `"CPU usage smooth`") * $ringAlpha, `"CPU usage`")"   'HAL' 'Timer'))
    $sources.Add((New-SourceLines 'GPU1 usage stage1' '%' '' "if(validate(`"GPU1 usage stage1`"), `"GPU1 usage stage1`" + (`"GPU1 usage`" - `"GPU1 usage stage1`") * $ringAlpha, `"GPU1 usage`")"     'HAL' 'Timer'))
    $sources.Add((New-SourceLines 'GPU1 usage smooth' '%' '' "if(validate(`"GPU1 usage smooth`"), `"GPU1 usage smooth`" + (`"GPU1 usage stage1`" - `"GPU1 usage smooth`") * $ringAlpha, `"GPU1 usage`")" 'HAL' 'Timer'))

    # 4. Merge layers from all four levels, adding VisibilitySource, rewriting sprite coordinates,
    #    and setting per-layer RefreshPeriod (16 ms for rings, 1000 ms for 1%/0.1% low, 250 ms for all others
    #    so non-ring layers stay snapshotted during the intermediate 60 FPS ring ticks).
    $mergedLayers = [System.Collections.Generic.List[string[]]]::new()

    foreach ($def in $levelDefs) {
        $sections = Read-IniSections $def.Path
        foreach ($sec in ($sections | Where-Object { $_.Name -match '^Layer\d+$' })) {
            $outLines = [System.Collections.Generic.List[string]]::new()
            $insertedVis = $false

            $hasLowMetric = $false
            $hasSmoothRing = $false
            foreach ($line in $sec.Lines) {
                if ($line -match '^VisibilitySource=') { continue }

                if ($line -match '^Text=(.*)$') {
                    if (-not $insertedVis) {
                        $outLines.Add("VisibilitySource=$($def.Vis)")
                        $insertedVis = $true
                    }
                    $updated = Rewrite-SpriteTags $Matches[1] $def.OffsetX $def.OffsetY $def.Rings $totalWidth $totalHeight
                    if ($updated -match '%Framerate (1|0\.1)% Low%') { $hasLowMetric = $true }
                    if ($updated -match '<AI=(CPU|GPU1) usage smooth,') { $hasSmoothRing = $true }
                    $outLines.Add("Text=$updated")
                }
                else {
                    $outLines.Add($line)
                    if ($line -match '^Name=' -and -not $insertedVis) {
                        $outLines.Add("VisibilitySource=$($def.Vis)")
                        $insertedVis = $true
                    }
                }
            }

            if (-not ($outLines | Where-Object { $_ -match '^RefreshPeriod=' })) {
                if ($hasSmoothRing) {
                    $outLines.Add("RefreshPeriod=$ringRefresh")
                }
                elseif ($hasLowMetric) {
                    $outLines.Add('RefreshPeriod=1000')
                }
                else {
                    $outLines.Add("RefreshPeriod=$baseRefresh")
                }
            }

            if (-not $insertedVis) {
                $outLines.Insert(0, "VisibilitySource=$($def.Vis)")
            }

            $mergedLayers.Add($outLines.ToArray())
        }
    }

    if ($mergedLayers.Count -gt $maxLayers) {
        throw "$($theme.Folder): merged layer count $($mergedLayers.Count) exceeds MAX_LAYERS ($maxLayers)"
    }

    # 5. Assemble and write the combined .ovl file (Windows-1252, CRLF).
    $ovl = [System.Collections.Generic.List[string]]::new()
    $ovl.Add('[Master]')
    foreach ($l in $masterSection.Lines) { $ovl.Add($l) }

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
