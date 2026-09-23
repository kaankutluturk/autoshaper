# Builds autoshaper.exe (and autoshaper-cli.exe for --self-test) with the .NET Framework 4 compiler.
# 32-bit, so the leaderboard restore helper can load the game's 32-bit steam_api.dll.
param([string]$Out = "$PSScriptRoot\bin")

$ErrorActionPreference = 'Stop'
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { throw "C# compiler not found at $csc" }
New-Item -ItemType Directory -Force $Out | Out-Null

# csc fails with CS1619 when TEMP is a very long path; give it a short one.
$tmp = Join-Path $env:LOCALAPPDATA 'AutoShaperBuild'
New-Item -ItemType Directory -Force $tmp | Out-Null
$env:TMP = $tmp; $env:TEMP = $tmp

$sources = Get-ChildItem "$PSScriptRoot\src\*.cs" | ForEach-Object { $_.FullName }
& $csc /nologo /target:winexe /platform:x86 /optimize+ /unsafe "/win32icon:$PSScriptRoot\assets\autoshaper.ico" `
    "/out:$Out\autoshaper.exe" `
    /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Xml.dll `
    $sources
if ($LASTEXITCODE -ne 0) { throw "build failed" }

# Console build of the same sources, used to run --self-test with visible output.
& $csc /nologo /target:exe /platform:x86 /optimize+ /unsafe `
    "/out:$Out\autoshaper-cli.exe" `
    /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Xml.dll `
    $sources
if ($LASTEXITCODE -ne 0) { throw "console build failed" }
Write-Host "built $Out\autoshaper.exe"
