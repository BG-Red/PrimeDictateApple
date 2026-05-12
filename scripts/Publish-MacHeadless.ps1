param(
    [ValidateSet("osx-arm64", "osx-x64", "all")]
    [string]$Runtime = "osx-arm64",

    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$project = Join-Path $repoRoot "PrimeDictate.Headless\PrimeDictate.Headless.csproj"
$outputRoot = Join-Path $repoRoot "artifacts\macos"

$runtimes = if ($Runtime -eq "all") {
    @("osx-arm64", "osx-x64")
} else {
    @($Runtime)
}

foreach ($rid in $runtimes) {
    $output = Join-Path $outputRoot $rid
    dotnet publish $project `
        -c $Configuration `
        -r $rid `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:EnableCompressionInSingleFile=true `
        -o $output
}
