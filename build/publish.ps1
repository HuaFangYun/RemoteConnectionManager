<#
.SYNOPSIS
    Publishes RemoteConnectionManager to a folder and, when given a certificate, signs it.

.DESCRIPTION
    Framework dependent x64 build (needs the .NET 10 desktop runtime, like the PAW has).
    Signing is what lets the build past the Defender ASR rule "Block executable files unless they
    meet a prevalence, age, or trusted list criterion" without an exclusion (see docs/PLAN.md, risks).

    Only our own files are signed (RemoteConnectionManager*.exe/.dll). Third party files keep their
    publisher's signature. The script lists any binary that is still unsigned at the end, because
    the ASR rule checks DLLs as well.

.PARAMETER CertificateThumbprint
    Code signing certificate in Cert:\CurrentUser\My or Cert:\LocalMachine\My (an OV/EV cert, for
    example Certum, or a smart card / token certificate).

.PARAMETER TrustedSigningMetadata
    Path to an Azure Trusted Signing metadata.json. Needs the Trusted Signing client (dlib) installed;
    pass its path with -TrustedSigningDlib.

.EXAMPLE
    ./build/publish.ps1
    ./build/publish.ps1 -CertificateThumbprint 0123ABCD...
    ./build/publish.ps1 -TrustedSigningMetadata .\metadata.json -TrustedSigningDlib "$env:LOCALAPPDATA\Microsoft\MicrosoftTrustedSigningClientTools\Azure.CodeSigning.Dlib.dll"
#>
[CmdletBinding(DefaultParameterSetName = 'None')]
param(
    [string]$Output = (Join-Path $PSScriptRoot '..\artifacts\publish'),
    [string]$Configuration = 'Release',

    [Parameter(ParameterSetName = 'Certificate', Mandatory)]
    [string]$CertificateThumbprint,

    [Parameter(ParameterSetName = 'TrustedSigning', Mandatory)]
    [string]$TrustedSigningMetadata,
    [Parameter(ParameterSetName = 'TrustedSigning', Mandatory)]
    [string]$TrustedSigningDlib,

    [string]$TimestampUrl = 'http://timestamp.acs.microsoft.com'
)

$ErrorActionPreference = 'Stop'
$Output = [System.IO.Path]::GetFullPath($Output)
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$project = Join-Path $root 'src\RemoteConnectionManager.App\RemoteConnectionManager.App.csproj'

Write-Host "Publishing $Configuration to $Output"
if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }
dotnet publish $project -c $Configuration -r win-x64 --self-contained false -o $Output -p:DebugType=none
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

$ours = Get-ChildItem $Output -File | Where-Object { $_.Name -like 'RemoteConnectionManager*' -and $_.Extension -in '.exe', '.dll' }

if ($PSCmdlet.ParameterSetName -ne 'None') {
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
        Sort-Object { [version]$_.Directory.Parent.Name } -Descending | Select-Object -First 1
    if (-not $signtool) { throw 'signtool.exe not found. Install the Windows 10/11 SDK signing tools.' }

    $common = @('sign', '/v', '/fd', 'SHA256', '/tr', $TimestampUrl, '/td', 'SHA256')
    if ($PSCmdlet.ParameterSetName -eq 'Certificate') {
        $signArgs = $common + @('/sha1', $CertificateThumbprint)
    } else {
        $signArgs = $common + @('/dlib', $TrustedSigningDlib, '/dmdf', (Resolve-Path $TrustedSigningMetadata))
    }

    Write-Host "Signing $($ours.Count) files with $($signtool.FullName)"
    & $signtool.FullName @signArgs @($ours.FullName)
    if ($LASTEXITCODE -ne 0) { throw "signtool failed ($LASTEXITCODE)" }
}

$unsigned = Get-ChildItem $Output -Recurse -File -Include *.exe, *.dll |
    Where-Object { (Get-AuthenticodeSignature $_.FullName).Status -ne 'Valid' }
if ($unsigned) {
    Write-Warning "Not signed ($($unsigned.Count)). The PAW's ASR rule may block these unless the install folder is excluded:"
    $unsigned | ForEach-Object { Write-Host "  $($_.FullName.Substring($Output.Length).TrimStart('\'))" }
} else {
    Write-Host 'Every binary has a valid signature.'
}
Write-Host "Done: $Output"
