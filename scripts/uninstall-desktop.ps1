[CmdletBinding()]
param(
    [string]$InstallPath = (Join-Path $env:LOCALAPPDATA "Programs\ObjectAnalyzer")
)

$ErrorActionPreference = "Stop"

$startMenu = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\ObjectAnalyzer"
$shortcutPath = Join-Path $startMenu "Object Analyzer.lnk"

if (Test-Path $shortcutPath) {
    Remove-Item -LiteralPath $shortcutPath -Force
}

if (Test-Path $startMenu) {
    Remove-Item -LiteralPath $startMenu -Recurse -Force
}

if (Test-Path $InstallPath) {
    Remove-Item -LiteralPath $InstallPath -Recurse -Force
}

Write-Host "Object Analyzer Desktop has been uninstalled."
