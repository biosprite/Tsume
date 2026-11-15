<#
.SYNOPSIS
    Builds Tsume and starts it, replacing any copy already running.

.EXAMPLE
    tools\run.ps1                   # start with the panel open
    tools\run.ps1 -Background       # start hidden; summon with View + Menu
    tools\run.ps1 -Stay             # keep the panel open when it loses focus (for development)
#>
param(
    [switch] $Background,
    [switch] $Stay
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

Get-Process Tsume -ErrorAction SilentlyContinue | Stop-Process -Force

& (Join-Path $PSScriptRoot 'build.ps1')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$arguments = @()
if ($Background) { $arguments += '--background' }
if ($Stay)       { $arguments += '--stay' }

$tsume = Join-Path $root 'artifacts\bin\Tsume.App\release_win-x64\Tsume.exe'

if ($arguments) { Start-Process $tsume -ArgumentList $arguments }
else            { Start-Process $tsume }
