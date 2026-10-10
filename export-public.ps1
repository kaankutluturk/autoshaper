param([string]$OutputDirectory = (Join-Path $PSScriptRoot '..\..\release\public'), [string]$LicenseHolder = 'AutoShaper contributors')
$ErrorActionPreference = 'Stop'
# Allowlist: only what the public repository needs. NOTES.md (internal notes) and anything outside this
# folder (research material, work files) are never exported.
$include = @('src', 'tools', 'assets', 'build.ps1', 'package.ps1', 'README.md', '.gitignore')
if (Test-Path -LiteralPath $OutputDirectory) {
    if ((Get-ChildItem -LiteralPath $OutputDirectory -Force).Count -gt 0) { throw "Export target is not empty. Use a fresh directory: $OutputDirectory" }
} else {
    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
}
foreach ($name in $include) {
    $source = Join-Path $PSScriptRoot $name
    if (!(Test-Path -LiteralPath $source)) { throw "Expected source missing: $name" }
    Copy-Item -LiteralPath $source -Destination (Join-Path $OutputDirectory $name) -Recurse -Force
}
$licenseText = @"
MIT License

Copyright (c) $((Get-Date).Year) $LicenseHolder

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
"@
Set-Content -LiteralPath (Join-Path $OutputDirectory 'LICENSE') -Value $licenseText -NoNewline
Write-Output "Exported to $OutputDirectory"
Write-Output "Included: $($include -join ', '), LICENSE"
Write-Output "LICENSE holder: '$LicenseHolder' (pass -LicenseHolder to set a real name before publishing)"
