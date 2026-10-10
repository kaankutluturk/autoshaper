# Regenerates the original pixel-art icon with a metal eye housing.
param(
    [string]$Source = (Join-Path $PSScriptRoot '..\assets\autoshaper-source.png'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\assets')
)
$ErrorActionPreference = 'Stop'
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$tmp = Join-Path $env:LOCALAPPDATA 'AutoShaperBuild'
New-Item -ItemType Directory -Force $tmp | Out-Null
$env:TMP = $tmp; $env:TEMP = $tmp

$make = Join-Path $tmp 'makeicon.exe'
& $csc /nologo /reference:System.Drawing.dll "/out:$make" "$PSScriptRoot\MakeIcon.cs"
if ($LASTEXITCODE -ne 0) { throw 'icon tool build failed' }
& $make $Source $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'icon generation failed' }
