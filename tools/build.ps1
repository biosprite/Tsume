<#
.SYNOPSIS
    Builds Tsume.

.EXAMPLE
    tools\build.ps1                 # Release build
    tools\build.ps1 -Configuration Debug
#>
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

# This machine has .NET runtimes system-wide but the SDK only in the user profile.
$userDotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
$dotnet = if (Test-Path $userDotnet) { $userDotnet } else { 'dotnet' }

$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

& $dotnet build (Join-Path $root 'Tsume.slnx') --configuration $Configuration --verbosity quiet
exit $LASTEXITCODE
