#Requires -Version 5.1
<#
.SYNOPSIS
  Publishes a self-contained win-x64 build to artifacts\win-x64\publish.
#>
param(
    [string] $Configuration = "Release",
    [string] $PackageVersion,
    [string] $AssemblyVersion,
    [string] $FileVersion,
    [string] $InformationalVersion
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$publishDir = Join-Path $repoRoot "artifacts\win-x64\publish"

Push-Location $repoRoot
try {
    if (Test-Path $publishDir) {
        Remove-Item -Recurse -Force $publishDir
    }

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

    dotnet publish .\PrimeDictate.csproj `
        -c $Configuration `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=false `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        $msbuildProps `
        -o $publishDir

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE"
    }

    Write-Host "Published to $publishDir"
}
finally {
    Pop-Location
}
