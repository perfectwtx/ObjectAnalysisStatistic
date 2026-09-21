[CmdletBinding()]
param(
    [ValidateSet("win-x64")]
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "src/ObjectAnalyzer.Desktop/ObjectAnalyzer.Desktop.csproj"
$output = Join-Path $repoRoot "artifacts/desktop/$Runtime"
$zip = Join-Path $repoRoot "artifacts/ObjectAnalyzer-Desktop-$Runtime.zip"

if (-not (Test-Path $project)) {
    throw "Desktop project not found: $project"
}

if (Test-Path $output) {
    Remove-Item $output -Recurse -Force
}

if (Test-Path $zip) {
    Remove-Item $zip -Force
}

Push-Location $repoRoot
try {
    $publishArgs = @(
        "publish",
        $project,
        "-c", $Configuration,
        "-r", $Runtime,
        "--self-contained", "true",
        "-p:PublishSingleFile=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-p:DebugType=None",
        "-p:DebugSymbols=false",
        "-o", $output
    )

    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE"
    }

    Compress-Archive -Path "$output/*" -DestinationPath $zip -CompressionLevel Optimal
}
finally {
    Pop-Location
}

Write-Host "Desktop package created:"
Write-Host "  $zip"
