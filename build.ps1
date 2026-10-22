# Builds Sparking Unverum and creates dist/SparkingUnverum-v<version>.zip, the file attached to GitHub releases
# Usage: ./build.ps1                 (version from SparkingUnverum.csproj)
#        ./build.ps1 -Version 1.0.0  (or 1.0.0-beta)
param([string]$Version, [string]$OutDir)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'SparkingUnverum\SparkingUnverum.csproj'

if (-not $Version) {
    $Version = ([xml](Get-Content $project)).Project.PropertyGroup.InformationalVersion | Where-Object { $_ } | Select-Object -First 1
}
$Version = $Version.TrimStart('v', 'V')
$number = ($Version -split '-')[0]
Write-Host "Building Sparking Unverum v$Version"

$dist = if ($OutDir) { $OutDir } else { Join-Path $root 'dist' }
New-Item -ItemType Directory -Force $dist | Out-Null

# Two packages: the small one needs the .NET 10 Desktop Runtime, the standalone one carries it inside
foreach ($variant in @(@{ Name = ''; SelfContained = 'false' }, @{ Name = '-standalone'; SelfContained = 'true' })) {
    $publish = Join-Path $root "publish\SparkingUnverum$($variant.Name)"
    Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue
    dotnet publish $project -c Release -r win-x64 --self-contained $($variant.SelfContained) -o $publish `
        -p:Version=$number -p:AssemblyVersion="$number.0" -p:FileVersion="$number.0" -p:InformationalVersion=$Version `
        -p:DebugType=none
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

    $zip = Join-Path $dist "SparkingUnverum-v$Version$($variant.Name).zip"
    Remove-Item $zip -Force -ErrorAction SilentlyContinue
    # Files go at the root of the zip, the self updater copies them over the install folder
    Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip
    Write-Host "Created $zip"
}
