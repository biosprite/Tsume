<#
.SYNOPSIS
    Builds the Tsume installer: one .msi with everything in it.

.DESCRIPTION
    1. Publishes the panel and the helper, self-contained, into artifacts\publish.
    2. Signs the programs, if a certificate is given.
    3. Packs that folder into artifacts\installer\Tsume-<version>-x64.msi
    4. Signs the installer, if a certificate is given.

    The version comes from Directory.Build.props. Without -Certificate the build is unsigned,
    and SmartScreen will warn on other machines.

.PARAMETER Certificate
    Thumbprint of a code-signing certificate in your certificate store.
    Needs signtool.exe (Windows SDK).

.EXAMPLE
    tools\publish.ps1
    tools\publish.ps1 -Certificate 0123456789ABCDEF0123456789ABCDEF01234567
#>
param(
    [string] $Certificate,
    [string] $TimestampServer = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

# This machine has .NET runtimes system-wide but the SDK only in the user profile.
$userDotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
$dotnet = if (Test-Path $userDotnet) { $userDotnet } else { 'dotnet' }

$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

[xml] $shared = Get-Content (Join-Path $root 'Directory.Build.props')
$version = ($shared.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version

$files     = Join-Path $root 'artifacts\publish'
$installer = Join-Path $root 'artifacts\installer'

# Built apart from the everyday build, so publishing never trips over a running Tsume.
$scratch   = Join-Path $root 'artifacts\publish-build'

function Invoke-Step([string] $what, [scriptblock] $step) {
    Write-Host "== $what"
    & $step
    if ($LASTEXITCODE -ne 0) { throw "$what failed." }
}

function Sign([string[]] $paths) {
    if (-not $Certificate) { return }

    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object FullName -match '\\x64\\' | Sort-Object FullName | Select-Object -Last 1
    if (-not $signtool) { throw 'signtool.exe was not found. Install the Windows SDK to sign.' }

    & $signtool.FullName sign /sha1 $Certificate /fd SHA256 /tr $TimestampServer /td SHA256 /d 'Tsume' $paths
    if ($LASTEXITCODE -ne 0) { throw 'Signing failed.' }
}

if (Test-Path $files) { Remove-Item $files -Recurse -Force }

foreach ($project in 'Tsume.Helper', 'Tsume.App') {
    Invoke-Step "Publishing $project" {
        & $dotnet publish (Join-Path $root "src\$project\$project.csproj") `
            --configuration Release --self-contained true --output $files --verbosity quiet `
            --artifacts-path $scratch
    }
}

# Only Tsume's own files: the .NET runtime files are already signed by Microsoft.
Sign (Get-ChildItem $files -Filter 'Tsume*' | Where-Object Extension -in '.exe', '.dll' | ForEach-Object FullName)

Invoke-Step 'Building the installer' {
    & $dotnet build (Join-Path $root 'installer\Tsume.Installer.wixproj') `
        --configuration Release --verbosity quiet `
        "-p:PublishDir=$files" "-p:Version=$version" "-p:OutputPath=$installer\"
}

$msi = Join-Path $installer "Tsume-$version-x64.msi"

Sign @($msi)

Write-Host ''
Write-Host ('{0}   {1:N0} MB' -f $msi, ((Get-Item $msi).Length / 1MB))
if (-not $Certificate) {
    Write-Warning 'Unsigned build. Fine on your own device; SmartScreen will warn anyone else who runs it.'
}
