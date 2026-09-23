# Regenerates assets\autoshaper.ico and assets\autoshaper.png by editing
# WAVESHAPER's own icon. The game install is only read, never modified.
param([string]$Game = 'C:\Program Files (x86)\Steam\steamapps\common\WAVESHAPER\WAVESHAPER.exe')
$ErrorActionPreference = 'Stop'
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$tmp = Join-Path $env:LOCALAPPDATA 'AutoShaperBuild'
New-Item -ItemType Directory -Force $tmp | Out-Null
$env:TMP = $tmp; $env:TEMP = $tmp

$extract = Join-Path $tmp 'iconextract.exe'
& $csc /nologo /reference:System.Drawing.dll "/out:$extract" "$PSScriptRoot\IconExtract.cs"
if ($LASTEXITCODE -ne 0) { throw 'icon extractor build failed' }
& $extract $Game (Join-Path $tmp 'original') | Out-Null

$make = Join-Path $tmp 'makeicon.exe'
& $csc /nologo /reference:System.Drawing.dll "/out:$make" "$PSScriptRoot\MakeIcon.cs"
if ($LASTEXITCODE -ne 0) { throw 'icon tool build failed' }
& $make (Join-Path $tmp 'original\original-64.png') (Join-Path $PSScriptRoot '..\assets')
