$ErrorActionPreference = 'Stop'
$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$installerPath = Join-Path $toolsDir 'PrimeDictate-Online.msi'

$silentArgs = '/qn /norestart'

if (Test-Path $installerPath) {
  $packageArgs = @{
    packageName    = 'primedictate'
    fileType       = 'msi'
    file           = $installerPath
    silentArgs     = $silentArgs
    validExitCodes = @(0, 3010, 1605, 1614, 1641)
  }

  Uninstall-ChocolateyPackage @packageArgs
} else {
  Write-Warning "Bundled MSI not found at '$installerPath'. Attempting MSI uninstall via product name fallback."

  $fallbackArgs = @{
    packageName    = 'primedictate'
    softwareName   = 'PrimeDictate*'
    fileType       = 'msi'
    silentArgs     = $silentArgs
    validExitCodes = @(0, 3010, 1605, 1614, 1641)
  }

  Uninstall-ChocolateyPackage @fallbackArgs
}
