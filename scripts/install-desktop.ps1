[CmdletBinding()]
param(
    [string]$SourcePath = $PSScriptRoot,
    [string]$InstallPath = (Join-Path $env:LOCALAPPDATA "Programs\ObjectAnalyzer")
)

$ErrorActionPreference = "Stop"

$source = (Resolve-Path $SourcePath).Path
$exe = Join-Path $source "ObjectAnalyzer.Desktop.exe"

if (-not (Test-Path $exe)) {
    throw "ObjectAnalyzer.Desktop.exe was not found in: $source"
}

if (Test-Path $InstallPath) {
    Get-ChildItem -LiteralPath $InstallPath -Force | Remove-Item -Recurse -Force
}
else {
    New-Item -ItemType Directory -Path $InstallPath -Force | Out-Null
}

Copy-Item -LiteralPath (Join-Path $source "*") -Destination $InstallPath -Recurse -Force

$startMenu = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\ObjectAnalyzer"
New-Item -ItemType Directory -Path $startMenu -Force | Out-Null

$shortcutPath = Join-Path $startMenu "Object Analyzer.lnk"
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = Join-Path $InstallPath "ObjectAnalyzer.Desktop.exe"
$shortcut.WorkingDirectory = $InstallPath
$shortcut.Description = "Object Analyzer Desktop"
$shortcut.Save()

Write-Host "Object Analyzer Desktop installed."
Write-Host "Install path: $InstallPath"
Write-Host "Start Menu:   $shortcutPath"
