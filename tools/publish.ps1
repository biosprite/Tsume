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

.PARAMETER Build
    A test build's number (CI passes its run number). It becomes the fourth part of the version,
    e.g. 1.0.100 -> 1.0.100.57. Windows Installer ignores that part, so a test build and the
    release it came from replace each other in one step (see MajorUpgrade in installer\Package.wxs).

.PARAMETER Certificate
    Thumbprint of a code-signing certificate in your certificate store.
    Needs signtool.exe (Windows SDK).

.EXAMPLE
    tools\publish.ps1
    tools\publish.ps1 -Build 57
    tools\publish.ps1 -Certificate 0123456789ABCDEF0123456789ABCDEF01234567
#>
param(
    [ValidateScript({ $null -eq $_ -or ($_ -ge 0 -and $_ -le 65534) })]
    [Nullable[int]] $Build = $null,
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

# Windows Installer compares only major.minor.patch (at most 255.255.65535), so a release has no fourth part.
$parts = $version.Split('.')
if ($parts.Count -lt 2 -or $parts.Count -gt 3 -or ($parts | Where-Object { $_ -notmatch '^\d+$' })) {
    throw "Version '$version' in Directory.Build.props must be major.minor or major.minor.patch."
}
if ([int] $parts[0] -gt 255 -or [int] $parts[1] -gt 255 -or ($parts.Count -eq 3 -and [int] $parts[2] -gt 65535)) {
    throw "Version '$version' is out of range for Windows Installer (255.255.65535)."
}

if ($null -ne $Build) {
    # 1.0 -> 1.0.0.57: pad to three parts first, or the build number would land in the patch slot.
    while ($parts.Count -lt 3) { $parts += '0' }
    $version = ($parts + $Build) -join '.'
}

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
if (Test-Path $installer) { Remove-Item (Join-Path $installer '*.msi') -Force }

foreach ($project in 'Tsume.Helper', 'Tsume.App') {
    Invoke-Step "Publishing $project" {
        & $dotnet publish (Join-Path $root "src\$project\$project.csproj") `
            --configuration Release --self-contained true --output $files --verbosity quiet `
            --artifacts-path $scratch "-p:Version=$version"
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
