#Requires -Version 5.1
<#
.SYNOPSIS
  Publishes the app and builds WiX MSIs (offline and/or online) using only the .NET SDK.

.PARAMETER Installer
  Offline (bundles ggml-large-v3-turbo.bin), Online (downloads model via curl after install), or Both.

.PARAMETER SkipPublish
  Reuse existing artifacts\win-x64\publish without running dotnet publish.

.NOTES
  Requires .NET 8 SDK. WiX Toolset is restored via NuGet (WixToolset.Sdk); no separate WiX install needed.
#>
param(
    [ValidateSet("Offline", "Online", "Both")]
    [string] $Installer = "Both",

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
$modelRepoPath = Join-Path $repoRoot "models\ggml-large-v3-turbo.bin"
$offlineProj = Join-Path $repoRoot "installer\wix\offline\PrimeDictate.Offline.wixproj"
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

if ($Installer -eq "Offline" -or $Installer -eq "Both") {
    if (-not (Test-Path $modelRepoPath)) {
        throw @"
Offline MSI requires the Whisper model at:
  $modelRepoPath

Download once (see README), then re-run. Or build only the online MSI:
  .\scripts\Build-Installers.ps1 -Installer Online
"@
    }
    $modelDirFull = (Resolve-Path (Split-Path $modelRepoPath -Parent)).Path
    Write-Host "Building offline MSI..."
    dotnet build $offlineProj -c Release "-p:PublishDir=$publishDirFull" "-p:ModelSourceDir=$modelDirFull" $msbuildProps
    if ($LASTEXITCODE -ne 0) {
        throw "Offline WiX build failed with exit code $LASTEXITCODE"
    }
    $offlineMsi = Join-Path $repoRoot "installer\wix\offline\bin\Release\PrimeDictate-$version-Windows-Offline.msi"
    Copy-Item -Force $offlineMsi (Join-Path $outDir (Split-Path $offlineMsi -Leaf))
}

if ($Installer -eq "Online" -or $Installer -eq "Both") {
    Write-Host "Building online MSI..."
    dotnet build $onlineProj -c Release "-p:PublishDir=$publishDirFull" $msbuildProps
    if ($LASTEXITCODE -ne 0) {
        throw "Online WiX build failed with exit code $LASTEXITCODE"
    }
    $onlineMsi = Join-Path $repoRoot "installer\wix\online\bin\Release\PrimeDictate-$version-Windows-Online.msi"
    Copy-Item -Force $onlineMsi (Join-Path $outDir (Split-Path $onlineMsi -Leaf))
}

Write-Host "Done. MSIs: $outDir"
