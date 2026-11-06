<#
.SYNOPSIS
    Publishes Remote Connection Manager and packs it into a setup exe with Inno Setup.

.DESCRIPTION
    The local equivalent of .github/workflows/build.yml. Keep the two in step: both call
    build/publish.ps1 and then compile build/installer/RemoteConnectionManager.iss.

    Needs Inno Setup 6 (winget install JRSoftware.InnoSetup). Any extra parameters, such as
    -CertificateThumbprint, are passed straight to publish.ps1 so the app files can be signed
    before they are packed.

.EXAMPLE
    ./build/build-installer.ps1
    ./build/build-installer.ps1 -Version 0.2.0
    ./build/build-installer.ps1 -Version 0.2.0 -CertificateThumbprint 0123ABCD...
#>
[CmdletBinding()]
param(
    [string]$Version,
    [Parameter(ValueFromRemainingArguments)]
    [object[]]$PublishArgs = @()
)

$ErrorActionPreference = 'Stop'
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$publishDir = Join-Path $root 'artifacts\publish'
$installerDir = Join-Path $root 'artifacts\installer'

if (-not $Version) {
    $Version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version
}
Write-Host "Version $Version"

& (Join-Path $PSScriptRoot 'publish.ps1') -Output $publishDir -Version $Version @PublishArgs

$iscc = @(
    (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw 'ISCC.exe not found. Install Inno Setup 6: winget install JRSoftware.InnoSetup' }

if (Test-Path $installerDir) { Remove-Item $installerDir -Recurse -Force }
& $iscc /Q "/DAppVersion=$Version" "/DSourceDir=$publishDir" "/DOutputDir=$installerDir" (Join-Path $PSScriptRoot 'installer\RemoteConnectionManager.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)" }

Get-ChildItem $installerDir -Filter *.exe | ForEach-Object { Write-Host "Installer: $($_.FullName)" }
