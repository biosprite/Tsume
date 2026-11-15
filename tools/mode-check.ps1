<#
.SYNOPSIS
    Switches performance mode, checks it took effect, and puts everything back.

.DESCRIPTION
    Runs `Tsume.Helper --mode-check` (one UAC prompt): applies Balanced and then Endurance (MSI's stock presets),
    reads the registers back and measures processor power draw under a few seconds of full load in each,
    then restores the starting mode and limits. Takes about 35 seconds; the fans will spin up.
    The report is printed and saved to artifacts\mode-check.txt.

    Run with MSI Center M paused, and after tools\write-check.ps1.

.PARAMETER NoBuild
    Skips the build, for when the running panel holds its files open.
#>
param([switch] $NoBuild)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

if (-not $NoBuild) {
    & (Join-Path $PSScriptRoot 'build.ps1')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$helper = Join-Path $root 'artifacts\bin\Tsume.Helper\release_win-x64\Tsume.Helper.exe'
$report = Join-Path $root 'artifacts\mode-check.txt'

Remove-Item $report -ErrorAction SilentlyContinue

Start-Process $helper -ArgumentList '--mode-check', "`"$report`"" -Wait -WindowStyle Hidden

if (Test-Path $report) { Get-Content $report -Encoding UTF8 }
else { Write-Warning 'No report was written. Was the UAC prompt declined?' }
