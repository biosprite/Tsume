<#
.SYNOPSIS
    Proves an unelevated program can drive the elevated helper over its pipe.

.DESCRIPTION
    Starts `Tsume.Helper --serve-for 40` (one UAC prompt), then from this unelevated shell:

        1. asks for a snapshot
        2. sends an invalid request   (must be refused, and must not stop the helper)
        3. switches to Balanced       (the reply must show Balanced, 25 W / 37 W)
        4. restores what step 1 found

    Each exchange is one line of JSON each way. The helper stops by itself after 40 s.

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

function Ask([string] $request) {
    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'Tsume.Helper.v1', [System.IO.Pipes.PipeDirection]::InOut)
    try {
        $pipe.Connect(3000)
        $utf8 = New-Object System.Text.UTF8Encoding($false)
        $writer = New-Object System.IO.StreamWriter($pipe, $utf8); $writer.AutoFlush = $true
        $reader = New-Object System.IO.StreamReader($pipe, $utf8)
        $writer.WriteLine($request)
        $reply = $reader.ReadLine()
    }
    finally { $pipe.Dispose() }

    Write-Host "  ->  $request"
    Write-Host "  <-  $reply"
    Write-Host ''
    $reply | ConvertFrom-Json
}

Start-Process $helper -ArgumentList '--serve-for', '40' -WindowStyle Hidden
Write-Host 'Waiting for the helper (accept the UAC prompt)...'

$found = $null
foreach ($attempt in 1..60) {
    try { $found = Ask '{"op":"snapshot.get"}'; break } catch { Start-Sleep -Milliseconds 500 }
}
if (-not $found) { Write-Warning 'The helper never answered. Was the UAC prompt declined?'; exit 1 }

$nonsense = Ask '{"op":"register.write","address":80,"value":99}'
$balanced = Ask '{"op":"performance.set","mode":"Balanced","manual":{"sustained":25,"boost":37}}'

$was = $found.snapshot
$restore = '{{"op":"performance.set","mode":"{0}","manual":{{"sustained":{1},"boost":{2}}}}}' -f $was.mode, $was.limits.sustained, $was.limits.boost
$restored = Ask $restore

$verdicts = [ordered]@{
    'unelevated client reached the helper'  = [bool] $found.ok
    'a request outside the list is refused' = (-not $nonsense.ok)
    'Balanced was applied'                  = ($balanced.snapshot.mode -eq 'Balanced' -and $balanced.snapshot.limits.sustained -eq 25 -and $balanced.snapshot.limits.boost -eq 37)
    'the starting state is back'            = ($restored.snapshot.mode -eq $was.mode -and $restored.snapshot.limits.sustained -eq $was.limits.sustained -and $restored.snapshot.limits.boost -eq $was.limits.boost)
}
foreach ($check in $verdicts.GetEnumerator()) {
    Write-Host ('  {0,-40} {1}' -f $check.Key, $(if ($check.Value) { 'yes' } else { 'NO' }))
}
