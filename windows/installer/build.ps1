#!/usr/bin/env pwsh
# Build a self-contained, single-folder NotyWin release, then bundle it
# into an Inno Setup installer.
#
# Requires: .NET 10 SDK, Inno Setup 6 (`iscc` on PATH).

param(
    [string]$Configuration = "Release",
    [string]$Rid = "win-x64",
    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\NotyWin.App\NotyWin.App.csproj"

Write-Host "Publishing NotyWin.App ($Rid, $Configuration, self-contained)..."
dotnet publish $project `
    --configuration $Configuration `
    --runtime $Rid `
    --self-contained true `
    --property:PublishSingleFile=false `
    --property:WindowsAppSDKSelfContained=true `
    --property:WindowsPackageType=None

$publishDir = Join-Path $root "src\NotyWin.App\bin\$Configuration\net10.0-windows10.0.26100.0\$Rid\publish"
if (!(Test-Path $publishDir)) {
    throw "Publish directory not found: $publishDir"
}

if ($SkipInstaller) {
    Write-Host "Skipping installer build (--SkipInstaller)."
    exit 0
}

$iscc = (Get-Command iscc -ErrorAction SilentlyContinue)?.Source
if (!$iscc) {
    $iscc = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
}
if (!(Test-Path $iscc)) {
    throw "Inno Setup (iscc) not found. Install from https://jrsoftware.org/isdl.php"
}

Write-Host "Running Inno Setup..."
& $iscc (Join-Path $root "installer\NotyWin.iss")

$installer = Join-Path $root "installer\Output\NotyWin-Setup-1.0.0.exe"
if (Test-Path $installer) {
    Write-Host "Installer built: $installer"
}
else {
    Write-Host "Installer output not found at $installer — check the iscc log."
}
