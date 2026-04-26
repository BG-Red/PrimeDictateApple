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

function Get-MsiCompatibleVersion {
    param([string] $Version)

    if ([string]::IsNullOrWhiteSpace($Version)) {
        throw "Version is required to compute an MSI-compatible package version."
    }

    $normalizedVersion = $Version.Trim()
    $match = [regex]::Match($normalizedVersion, '^(?<core>\d+\.\d+\.\d+)')
    if (-not $match.Success) {
        throw "Version '$Version' does not start with a semver core like 1.2.3."
    }

    return $match.Groups['core'].Value
}

function Get-ChocoCompatibleVersion {
    param([string] $Version)

    if ([string]::IsNullOrWhiteSpace($Version)) {
        throw "Version is required to compute a Chocolatey package version."
    }

    $normalizedVersion = $Version.Trim()
    if ($normalizedVersion -match '^\d+\.\d+\.\d+(-[0-9A-Za-z][0-9A-Za-z\.-]*)?$') {
        return $normalizedVersion
    }

    return Get-MsiCompatibleVersion -Version $normalizedVersion
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$publishDir = Join-Path $repoRoot "artifacts\win-x64\publish"
$onlineProj = Join-Path $repoRoot "installer\wix\online\PrimeDictate.Online.wixproj"
$outDir = Join-Path $repoRoot "artifacts\installer"
$version = if ([string]::IsNullOrWhiteSpace($PackageVersion)) { Get-RepoVersion -RepoRoot $repoRoot } else { $PackageVersion }
$msiVersion = Get-MsiCompatibleVersion -Version $version
$chocoVersion = Get-ChocoCompatibleVersion -Version $version
$msbuildProps = @()
if (-not [string]::IsNullOrWhiteSpace($msiVersion)) {
    $msbuildProps += "-p:Version=$msiVersion"
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
$builtOnlineMsi = Join-Path $repoRoot "installer\wix\online\bin\Release\PrimeDictate-$msiVersion-Windows-Online.msi"
$publishedOnlineMsi = Join-Path $outDir "PrimeDictate-$version-Windows-Online.msi"
Copy-Item -Force $builtOnlineMsi $publishedOnlineMsi

Write-Host "Building Chocolatey package..."
$chocoDir = Join-Path $repoRoot "installer\chocolatey"
$chocoToolsDir = Join-Path $chocoDir "tools"
if (-not (Test-Path $chocoToolsDir)) {
    New-Item -ItemType Directory -Force -Path $chocoToolsDir | Out-Null
}
$chocoMsiDest = Join-Path $chocoToolsDir "PrimeDictate-Online.msi"

# Copy the generated MSI into the Chocolatey tools directory
Copy-Item -Force $publishedOnlineMsi $chocoMsiDest

try {
    if (Get-Command choco -ErrorAction SilentlyContinue) {
        $originalLocation = Get-Location
        try {
            Set-Location $chocoDir
            choco pack "primedictate.nuspec" --version $chocoVersion
            if ($LASTEXITCODE -ne 0) {
                Write-Warning "Chocolatey pack failed with exit code $LASTEXITCODE"
            } else {
                $nupkgFile = Join-Path $chocoDir "primedictate.$chocoVersion.nupkg"
                if (Test-Path $nupkgFile) {
                    Copy-Item -Force $nupkgFile $outDir
                    Write-Host "Chocolatey package copied to $outDir"
                }
            }
        } finally {
            Set-Location $originalLocation
        }
    } else {
        Write-Warning "choco.exe not found in PATH. Skipping Chocolatey packaging. Tools and MSI are prepared in $chocoDir."
    }
} finally {
    Remove-Item -Path $chocoMsiDest -Force -ErrorAction SilentlyContinue
}

Write-Host "Done. Artifacts available in: $outDir"
