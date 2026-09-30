<#
.SYNOPSIS
    Builds a distributable release package of Disk Analyzer (self-contained,
    single-file win-x64 exe) and drops it under dist\release.

.EXAMPLE
    .\tools\publish-release.ps1
    .\tools\publish-release.ps1 -Version 0.1.6
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\DiskAnalyzer.App\DiskAnalyzer.App.csproj"

if (-not $Version) {
    [xml]$csprojXml = Get-Content $project
    $Version = $csprojXml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
if (-not $Version) {
    throw "Could not determine version from $project. Pass -Version explicitly."
}

$dotnet = Join-Path $root ".dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }

$releaseDir = Join-Path $root "dist\release"
$publishDir = Join-Path $releaseDir "$Version\$Runtime"
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

Write-Host "Publishing Disk Analyzer v$Version ($Runtime, self-contained, single-file)..."

& $dotnet publish $project `
    -c Release `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -p:InformationalVersion=$Version `
    -o $publishDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$zipName = "DiskAnalyzer-v$Version-$Runtime.zip"
$zipPath = Join-Path $releaseDir $zipName
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath

# Standalone exe asset (fixed name) for in-app auto-update; the zip is kept for manual install / older clients.
$exeName = "DiskAnalyzer.exe"
$exePath = Join-Path $releaseDir $exeName
Copy-Item (Join-Path $publishDir $exeName) $exePath -Force

$hash = (Get-FileHash -Path $zipPath -Algorithm SHA256).Hash
$exeHash = (Get-FileHash -Path $exePath -Algorithm SHA256).Hash
$sumsPath = Join-Path $releaseDir "SHA256SUMS.txt"
# Only this release's two files - stale lines from earlier versions would point at zips that are not uploaded.
@("$hash  $zipName", "$exeHash  $exeName") | Set-Content -Path $sumsPath -Encoding utf8

Write-Host ""
Write-Host "Release package created:"
Write-Host "  $zipPath"
Write-Host "  SHA256: $hash"
Write-Host "  $exePath"
Write-Host "  SHA256: $exeHash"
Write-Host "  (recorded in $sumsPath)"
