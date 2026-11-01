#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Builds Orrery's portable packages: a self-contained folder per platform that needs no .NET installed,
    zipped into artifacts/.

.DESCRIPTION
    Windows and Linux: Orrery-<version>-<rid>-portable.zip, with the app as a single executable, its native
    libraries (Skia, HarfBuzz, ANGLE, IronRDP, libsodium) beside it, LICENSE, THIRD-PARTY-NOTICES.txt and an empty
    orrery.portable.json, so the first launch asks where to keep its data (docs/design-storage-vault-import.md §2.1).

    macOS: Orrery-<version>-<rid>-portable.zip holding Orrery.app (Info.plist, icon) with the marker beside it,
    since the marker belongs next to the bundle, never inside it. The bundle isn't signed or notarized, so
    macOS asks before the first launch.

.EXAMPLE
    ./build/package.ps1                       # every platform
    ./build/package.ps1 -Runtimes win-x64     # just Windows x64
#>
param(
    [string[]] $Runtimes = @('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-arm64', 'osx-x64'),
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src/Orrery.App/Orrery.App.csproj'
$artifacts = Join-Path $root 'artifacts'
$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

New-Item -ItemType Directory -Force $artifacts | Out-Null

foreach ($rid in $Runtimes) {
    $name = "Orrery-$version-$rid-portable"
    $stage = Join-Path $artifacts $name
    $zip = Join-Path $artifacts "$name.zip"
    Remove-Item -Recurse -Force $stage, $zip -ErrorAction SilentlyContinue

    Write-Host "== $rid" -ForegroundColor Cyan
    $publish = Join-Path $artifacts "publish/$rid"
    dotnet publish $project -c $Configuration -r $rid --self-contained true -o $publish -p:DebugType=none -p:DebugSymbols=false -p:OrreryLockedRestore=true
    if ($LASTEXITCODE -ne 0) { throw "Publishing $rid failed." }
    & (Join-Path $PSScriptRoot 'Generate-ThirdPartyNotices.ps1') -PublishDir $publish

    # The notices come from the ordinary publish's deps.json, which a single-file publish keeps inside the executable.
    # The analyzer is off because it would add the ILLink package, which isn't in the lock files; App.Restart
    # only reads Assembly.Location when run through dotnet, never from the single file.
    # The bundle is neither compressed nor self-extracting: a packed executable that unpacks DLLs to a cache folder on
    # first launch is what antivirus heuristics flag (Defender's Trojan:Script/Wacatac.B!ml on Edge downloads), so the
    # native libraries ship beside the executable and the managed ones load straight from it.
    if ($rid -notlike 'osx-*') {
        $single = Join-Path $artifacts "publish/$rid-single"
        dotnet publish $project -c $Configuration -r $rid --self-contained true -o $single -p:DebugType=none -p:DebugSymbols=false -p:OrreryLockedRestore=true `
            -p:PublishSingleFile=true -p:EnableSingleFileAnalyzer=false -p:IncludeNativeLibrariesForSelfExtract=false -p:EnableCompressionInSingleFile=false
        if ($LASTEXITCODE -ne 0) { throw "Publishing $rid as a single file failed." }
        Copy-Item (Join-Path $publish 'THIRD-PARTY-NOTICES.txt') $single
        $publish = $single
    }

    New-Item -ItemType Directory -Force $stage | Out-Null
    if ($rid -like 'osx-*') {
        $bundle = Join-Path $stage 'Orrery.app/Contents'
        New-Item -ItemType Directory -Force (Join-Path $bundle 'MacOS'), (Join-Path $bundle 'Resources') | Out-Null
        Copy-Item -Recurse (Join-Path $publish '*') (Join-Path $bundle 'MacOS')
        Copy-Item (Join-Path $root 'src/Orrery.App/Assets/orrery.icns') (Join-Path $bundle 'Resources/Orrery.icns')
        @"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Orrery</string>
  <key>CFBundleDisplayName</key><string>Orrery</string>
  <key>CFBundleIdentifier</key><string>app.orrery.Orrery</string>
  <key>CFBundleVersion</key><string>$version</string>
  <key>CFBundleShortVersionString</key><string>$version</string>
  <key>CFBundleExecutable</key><string>Orrery</string>
  <key>CFBundleIconFile</key><string>Orrery.icns</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSHumanReadableCopyright</key><string>Copyright © 2026 InspireNXE. MIT License.</string>
</dict>
</plist>
"@ | Set-Content -Encoding utf8NoBOM (Join-Path $bundle 'Info.plist')
    }
    else {
        Copy-Item -Recurse (Join-Path $publish '*') $stage
    }

    Copy-Item (Join-Path $root 'LICENSE') $stage
    # Also beside the app on macOS, where the publish output (with its copy) is inside the bundle.
    if ($rid -like 'osx-*') { Copy-Item (Join-Path $publish 'THIRD-PARTY-NOTICES.txt') $stage }
    # Empty: portable mode, with the data location still to choose on first launch.
    New-Item -ItemType File (Join-Path $stage 'orrery.portable.json') | Out-Null

    # Zip archives keep no Unix permissions from Windows, so on Linux and macOS: chmod +x Orrery after unzipping.
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
    Write-Host "   $zip" -ForegroundColor Green
}

Remove-Item -Recurse -Force (Join-Path $artifacts 'publish') -ErrorAction SilentlyContinue
