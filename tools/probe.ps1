<#
.SYNOPSIS
    Asks the firmware what it currently reports. Reads only; changes nothing.

.DESCRIPTION
    Runs `Tsume.Helper --probe` (one UAC prompt) and prints the report, also saved to artifacts\probe.txt.
    Run it before trusting any firmware write: the values should match MSI Center M (mode, power limits, fan state).
#>

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

& (Join-Path $PSScriptRoot 'build.ps1')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$helper = Join-Path $root 'artifacts\bin\Tsume.Helper\release_win-x64\Tsume.Helper.exe'
$report = Join-Path $root 'artifacts\probe.txt'

Remove-Item $report -ErrorAction SilentlyContinue

# The helper's manifest requests elevation, so this is where the UAC prompt appears.
Start-Process $helper -ArgumentList '--probe', "`"$report`"" -Wait -WindowStyle Hidden

if (Test-Path $report) { Get-Content $report -Encoding UTF8 }
else { Write-Warning 'No report was written. Was the UAC prompt declined?' }
