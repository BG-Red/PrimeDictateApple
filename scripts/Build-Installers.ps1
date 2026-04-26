#Requires -Version 5.1
<#
.SYNOPSIS
  Publishes the app and builds the WiX online MSI using only the .NET SDK.

.PARAMETER Installer
  Online (downloads models after install). The offline installer is currently not built by this helper.

.PARAMETER SkipPublish
  Reuse existing artifacts\win-x64\publish without running dotnet publish.

.NOTES
  Requires .NET 8 SDK. WiX Toolset is restored via NuGet (WixToolset.Sdk); no separate WiX install needed.
#>
param(
    [ValidateSet("Online")]
    [string] $Installer = "Online",

    [switch] $SkipPublish,
    [string] $PackageVersion,
    [string] $AssemblyVersion,
    [string] $FileVersion,
    [string] $InformationalVersion
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-RepoVersion {
    param([string] $RepoRoot)
    [xml] $doc = Get-Content (Join-Path $RepoRoot "Directory.Build.props")
    $pg = @($doc.Project.PropertyGroup) | Where-Object { $_.Version } | Select-Object -First 1
    if (-not $pg -or -not $pg.Version) {
        throw "Could not read Version from Directory.Build.props"
    }
    return $pg.Version.Trim()
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$publishDir = Join-Path $repoRoot "artifacts\win-x64\publish"
$onlineProj = Join-Path $repoRoot "installer\wix\online\PrimeDictate.Online.wixproj"
$outDir = Join-Path $repoRoot "artifacts\installer"
$version = if ([string]::IsNullOrWhiteSpace($PackageVersion)) { Get-RepoVersion -RepoRoot $repoRoot } else { $PackageVersion }
$msbuildProps = @()
if (-not [string]::IsNullOrWhiteSpace($PackageVersion)) {
    $msbuildProps += "-p:Version=$PackageVersion"
}
if (-not [string]::IsNullOrWhiteSpace($AssemblyVersion)) {
    $msbuildProps += "-p:AssemblyVersion=$AssemblyVersion"
}
if (-not [string]::IsNullOrWhiteSpace($FileVersion)) {
    $msbuildProps += "-p:FileVersion=$FileVersion"
}
if (-not [string]::IsNullOrWhiteSpace($InformationalVersion)) {
    $msbuildProps += "-p:InformationalVersion=$InformationalVersion"
}

if (-not $SkipPublish) {
    & (Join-Path $PSScriptRoot "Publish-Windows.ps1") `
        -PackageVersion $PackageVersion `
        -AssemblyVersion $AssemblyVersion `
        -FileVersion $FileVersion `
        -InformationalVersion $InformationalVersion
}

if (-not (Test-Path (Join-Path $publishDir "PrimeDictate.exe"))) {
    throw "Publish output missing PrimeDictate.exe at $publishDir. Run without -SkipPublish."
}

New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$publishDirFull = (Resolve-Path $publishDir).Path

Write-Host "Building online MSI..."
dotnet build $onlineProj -c Release "-p:PublishDir=$publishDirFull" $msbuildProps
if ($LASTEXITCODE -ne 0) {
    throw "Online WiX build failed with exit code $LASTEXITCODE"
}
$onlineMsi = Join-Path $repoRoot "installer\wix\online\bin\Release\PrimeDictate-$version-Windows-Online.msi"
Copy-Item -Force $onlineMsi (Join-Path $outDir (Split-Path $onlineMsi -Leaf))

Write-Host "Done. MSIs: $outDir"
