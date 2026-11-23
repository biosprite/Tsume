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
    @{ Folder = 'Dark';  Suffix = ''       }
    @{ Folder = 'Smoke'; Suffix = '_smoke' }
    @{ Folder = 'White'; Suffix = '_white' }
    @{ Folder = 'Float'; Suffix = '_float' }
)

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

function Offset-SpriteTags([string]$text, [int]$offsetY, [int]$atlasWidth, [int]$atlasHeight) {
    if ($offsetY -eq 0) { return $text }

    # <I=w,h,sx,sy,sw,sh[,rot]>  -> index 3 is dwSpriteY
    $text = [regex]::Replace($text, '<I=([^>]+)>', {
        param($m)
        $parts = $m.Groups[1].Value -split ','
        if ($parts.Length -lt 6) { return $m.Value }
        $sx = [int]$parts[2]
        $sy = [int]$parts[3] + $offsetY
        $sw = [int]$parts[4]
        $sh = [int]$parts[5]
        if ($sx + $sw -gt $atlasWidth -or $sy + $sh -gt $atlasHeight) {
            throw "Sprite <I> out of stitched atlas bounds: x=$sx y=$sy w=$sw h=$sh vs ${atlasWidth}x${atlasHeight}"
        }
        $parts[3] = [string]$sy
        return '<I=' + ($parts -join ',') + '>'
    })

    # <AI=source,w,h,min,max,sx,sy,sw,sh,num,perLine[,bias,rotMin,rotMax]> -> index 6 is dwSpriteY
    $text = [regex]::Replace($text, '<AI=([^>]+)>', {
        param($m)
        $parts = $m.Groups[1].Value -split ','
        if ($parts.Length -lt 11) { return $m.Value }
        $sy = [int]$parts[6] + $offsetY
        $parts[6] = [string]$sy
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

    # 1. Stitch the three unique atlases vertically (FPS reuses Sleek's atlas at y = 0).
    $imgSleek   = [System.Drawing.Bitmap]::FromFile($sleekPngPath)
    $imgCompact = [System.Drawing.Bitmap]::FromFile($compactPngPath)
    $imgPerf    = [System.Drawing.Bitmap]::FromFile($perfPngPath)

    try {
        $offsetSleek   = 0
        $offsetCompact = $imgSleek.Height
        $offsetPerf    = $imgSleek.Height + $imgCompact.Height
        $totalWidth    = [Math]::Max($imgSleek.Width, [Math]::Max($imgCompact.Width, $imgPerf.Width))
        $totalHeight   = $offsetPerf + $imgPerf.Height

        $stitched = New-Object System.Drawing.Bitmap($totalWidth, $totalHeight, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $g = [System.Drawing.Graphics]::FromImage($stitched)
            try {
                $g.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
                $g.Clear([System.Drawing.Color]::Transparent)
                $g.DrawImageUnscaled($imgSleek,   0, $offsetSleek)
                $g.DrawImageUnscaled($imgCompact, 0, $offsetCompact)
                $g.DrawImageUnscaled($imgPerf,    0, $offsetPerf)
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

    # 2. Parse the four source layouts.
    $levelDefs = @(
        @{ Level = 1; Vis = 'Tsume level 1 visible'; Path = (Join-Path $dir "essential_fps${sfx}.ovl");                 OffsetY = $offsetSleek   }
        @{ Level = 2; Vis = 'Tsume level 2 visible'; Path = (Join-Path $dir "essential_sleek${sfx}.ovl");               OffsetY = $offsetSleek   }
        @{ Level = 3; Vis = 'Tsume level 3 visible'; Path = (Join-Path $dir "essential_sleek_compact${sfx}.ovl");       OffsetY = $offsetCompact }
        @{ Level = 4; Vis = 'Tsume level 4 visible'; Path = (Join-Path $dir "essential_performance_overlay${sfx}.ovl"); OffsetY = $offsetPerf    }
    )

    $perfSections = Read-IniSections $levelDefs[3].Path
    $masterSection = $perfSections | Where-Object { $_.Name -eq 'Master' } | Select-Object -First 1

    # 3. Build combined sources:
    #    - 4 AIDA visibility sources (x == 1..4 on TSUME_OVERLAY_LEVEL)
    #    - 4 raw HAL sources (updated BEFORE AIDA in OverlayDataSourcesList::UpdateSources)
    #    - 4 effective AIDA sources that use HAL when valid, falling back to Tsume AIDA values
    #    - remaining HAL sources from essential_performance_overlay unchanged
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

    # 4. Merge layers from all four levels, adding VisibilitySource and offsetting atlas Y coordinates.
    $mergedLayers = [System.Collections.Generic.List[string[]]]::new()

    foreach ($def in $levelDefs) {
        $sections = Read-IniSections $def.Path
        foreach ($sec in ($sections | Where-Object { $_.Name -match '^Layer\d+$' })) {
            $outLines = [System.Collections.Generic.List[string]]::new()
            $insertedVis = $false

            $hasLowMetric = $false
            foreach ($line in $sec.Lines) {
                if ($line -match '^VisibilitySource=') { continue }

                if ($line -match '^Text=(.*)$') {
                    if (-not $insertedVis) {
                        $outLines.Add("VisibilitySource=$($def.Vis)")
                        $insertedVis = $true
                    }
                    $updated = Offset-SpriteTags $Matches[1] $def.OffsetY $totalWidth $totalHeight
                    if ($updated -match '%Framerate (1|0\.1)% Low%') { $hasLowMetric = $true }
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

            if ($hasLowMetric -and -not ($outLines | Where-Object { $_ -match '^RefreshPeriod=' })) {
                $outLines.Add('RefreshPeriod=1000')
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
    $ovl.Add('RefreshPeriod=250')
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
