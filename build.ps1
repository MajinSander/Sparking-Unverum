# Builds Sparking Unverum and creates dist/SparkingUnverum-v<version>.zip, the file attached to GitHub releases
# Usage: ./build.ps1                 (version from SparkingUnverum.csproj)
#        ./build.ps1 -Version 1.0.0  (or 1.0.0-beta)
param([string]$Version)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'SparkingUnverum\SparkingUnverum.csproj'

if (-not $Version) {
    $Version = ([xml](Get-Content $project)).Project.PropertyGroup.InformationalVersion | Where-Object { $_ } | Select-Object -First 1
}
$Version = $Version.TrimStart('v', 'V')
$number = ($Version -split '-')[0]
Write-Host "Building Sparking Unverum v$Version"

$publish = Join-Path $root 'publish\SparkingUnverum'
$dist = Join-Path $root 'dist'
Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $dist | Out-Null

dotnet publish $project -c Release -r win-x64 --self-contained false -o $publish `
    -p:Version=$number -p:AssemblyVersion="$number.0" -p:FileVersion="$number.0" -p:InformationalVersion=$Version `
    -p:DebugType=none
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

$zip = Join-Path $dist "SparkingUnverum-v$Version.zip"
Remove-Item $zip -Force -ErrorAction SilentlyContinue
# Files go at the root of the zip, the self updater copies them over the install folder
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip
Write-Host "Created $zip"
