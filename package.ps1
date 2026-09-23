param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'release'))
$ErrorActionPreference = 'Stop'
$exePath = Join-Path $PSScriptRoot 'bin\autoshaper.exe'
if (!(Test-Path -LiteralPath $exePath -PathType Leaf)) { throw 'Missing release binary: bin\autoshaper.exe (run build.ps1 first)' }
if (Test-Path -LiteralPath $OutputDirectory) {
    foreach ($entry in Get-ChildItem -LiteralPath $OutputDirectory -Force) {
        if ($entry.PSIsContainer -or $entry.Name -ne 'autoshaper.exe') { throw "Release output is not empty. Use a fresh output directory: $OutputDirectory" }
    }
} else {
    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
}
$released = Join-Path $OutputDirectory 'autoshaper.exe'
Copy-Item -LiteralPath $exePath -Destination $released -Force
Write-Output "Released: $released"
Write-Output "Version: $((Get-Item -LiteralPath $released).VersionInfo.FileVersion)"
Write-Output "SHA256: $((Get-FileHash -LiteralPath $released -Algorithm SHA256).Hash)"
