<#
.SYNOPSIS
    Tests whether the firmware accepts writes — without changing anything.

.DESCRIPTION
    Runs `Tsume.Helper --write-check` (one UAC prompt): for each register Tsume uses, it reads the value,
    writes the same value back and reads it again. The report is printed and saved to artifacts\write-check.txt.
    Run tools\probe.ps1 first.
#>

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

& (Join-Path $PSScriptRoot 'build.ps1')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$helper = Join-Path $root 'artifacts\bin\Tsume.Helper\release_win-x64\Tsume.Helper.exe'
$report = Join-Path $root 'artifacts\write-check.txt'

Remove-Item $report -ErrorAction SilentlyContinue

Start-Process $helper -ArgumentList '--write-check', "`"$report`"" -Wait -WindowStyle Hidden

if (Test-Path $report) { Get-Content $report -Encoding UTF8 }
else { Write-Warning 'No report was written. Was the UAC prompt declined?' }
