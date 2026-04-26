$ErrorActionPreference = 'Stop'
$toolsDir   = $(Split-Path -parent $MyInvocation.MyCommand.Definition)
$fileLocation = Join-Path $toolsDir 'PrimeDictate-Online.msi'

$packageArgs = @{
  packageName    = 'primedictate'
  fileType       = 'msi'
  file           = $fileLocation
  silentArgs     = '/qn /norestart'
  validExitCodes = @(0, 3010, 1641)
}

Install-ChocolateyInstallPackage @packageArgs
