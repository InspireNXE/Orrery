#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Builds Orrery's Linux packages: .deb, .rpm and AppImage, for x64 and arm64, into artifacts/linux/.

.DESCRIPTION
    Publishes Orrery self-contained for each runtime (an ordinary multi-file publish, so nothing unpacks to
    ~/.net on first launch), generates THIRD-PARTY-NOTICES.txt, then:

    .deb and .rpm: built with nfpm (https://nfpm.goreleaser.com/), which runs on Windows, Linux and macOS and
    sets Unix file modes itself. The app goes to /opt/orrery, with /usr/bin/orrery linked to it, the launcher in
    /usr/share/applications and the icons in the hicolor theme. Installed copies run in installed mode, with
    data in ~/.config/orrery.

    AppImage: built with appimagetool, which only runs on Linux, so it's skipped elsewhere with a warning. The
    image is read-only and carries no orrery.portable.json, so it runs in installed mode too.

    nfpm and appimagetool are found on PATH, or pass -NfpmPath and -AppImageToolPath.

.EXAMPLE
    ./build/Build-LinuxPackages.ps1                                       # every format, x64 and arm64
    ./build/Build-LinuxPackages.ps1 -Runtimes linux-x64 -Formats deb,rpm   # just x64 .deb and .rpm
#>
param(
    [ValidateSet('linux-x64', 'linux-arm64')] [string[]] $Runtimes = @('linux-x64', 'linux-arm64'),
    [ValidateSet('deb', 'rpm', 'appimage')] [string[]] $Formats = @('deb', 'rpm', 'appimage'),
    [string] $Configuration = 'Release',
    [string] $NfpmPath,
    [string] $AppImageToolPath
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src/Orrery.App/Orrery.App.csproj'
$linux = Join-Path $root 'installer/linux'
$out = Join-Path $root 'artifacts/linux'
$work = Join-Path $out 'work'
$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

# Package dependencies. Self-contained .NET still needs the C runtime, ICU (globalization isn't invariant) and
# OpenSSL (TLS and crypto); Avalonia needs X11 and fontconfig. libsecret is only for keeping credentials in the
# system keyring, so it's recommended rather than required: without it Orrery uses the vault file.
$debDepends = @(
    'libc6', 'libgcc-s1', 'libstdc++6',
    'libicu78 | libicu77 | libicu76 | libicu74 | libicu72 | libicu70',
    'libssl3t64 | libssl3',
    'libfontconfig1', 'libx11-6', 'libice6', 'libsm6'
)
$rpmDepends = @('glibc', 'libgcc', 'libstdc++', 'libicu', 'openssl-libs', 'fontconfig', 'libX11', 'libICE', 'libSM')
$iconSizes = 16, 24, 32, 48, 64, 128, 256, 512
# Everything else in the publish is a library or data file.
$executables = 'Orrery', 'createdump'

function Find-Tool([string] $Explicit, [string] $Name, [string] $Hint) {
    if ($Explicit) {
        if (-not (Test-Path $Explicit)) { throw "'$Explicit' does not exist." }
        return $Explicit
    }
    $found = Get-Command $Name -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $found) { throw "$Name isn't on PATH. $Hint" }
    return $found.Source
}

# Single-quoted YAML: only the quote itself needs escaping.
function Yaml([string] $Text) { "'" + $Text.Replace("'", "''") + "'" }

function Write-NfpmConfig([string] $Rid, [string] $Publish, [string] $Path) {
    $arch = if ($Rid -eq 'linux-arm64') { 'arm64' } else { 'amd64' }
    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.AddRange([string[]]@(
        'name: orrery'
        "arch: $arch"
        'platform: linux'
        "version: $(Yaml $version)"
        "release: '1'"
        'section: net'
        'priority: optional'
        'maintainer: InspireNXE'
        'vendor: InspireNXE'
        'license: MIT'
        'description: |-'
        '  Remote connection manager for RDP, SSH, VNC and Telnet.'
        '  Keeps connections in one tree, opens them in tabs, and stores passwords in the'
        '  system keyring or an encrypted vault file. Imports mRemoteNG confCons.xml.'
        'contents:'
        '  - dst: /opt/orrery'
        '    type: dir'
        '    file_info: { mode: 0755 }'
    ))

    foreach ($file in Get-ChildItem $Publish -Recurse -File) {
        $relative = [System.IO.Path]::GetRelativePath($Publish, $file.FullName).Replace('\', '/')
        $mode = if ($executables -contains $relative) { '0755' } else { '0644' }
        $lines.Add("  - src: $(Yaml $file.FullName.Replace('\', '/'))")
        $lines.Add("    dst: $(Yaml "/opt/orrery/$relative")")
        $lines.Add("    file_info: { mode: $mode }")
    }

    $lines.AddRange([string[]]@(
        '  - src: /opt/orrery/Orrery'
        '    dst: /usr/bin/orrery'
        '    type: symlink'
        "  - src: $(Yaml (Join-Path $linux 'orrery.desktop').Replace('\', '/'))"
        '    dst: /usr/share/applications/orrery.desktop'
        '    file_info: { mode: 0644 }'
        '  - dst: /usr/share/doc/orrery'
        '    type: dir'
        '    file_info: { mode: 0755 }'
        "  - src: $(Yaml (Join-Path $root 'LICENSE').Replace('\', '/'))"
        '    dst: /usr/share/doc/orrery/LICENSE'
        '    file_info: { mode: 0644 }'
        "  - src: $(Yaml (Join-Path $Publish 'THIRD-PARTY-NOTICES.txt').Replace('\', '/'))"
        '    dst: /usr/share/doc/orrery/THIRD-PARTY-NOTICES.txt'
        '    file_info: { mode: 0644 }'
    ))
    foreach ($size in $iconSizes) {
        $lines.Add("  - src: $(Yaml (Join-Path $linux "icons/$size.png").Replace('\', '/'))")
        $lines.Add("    dst: /usr/share/icons/hicolor/${size}x${size}/apps/orrery.png")
        $lines.Add('    file_info: { mode: 0644 }')
    }

    $lines.Add('overrides:')
    $lines.Add('  deb:')
    $lines.Add('    depends:')
    foreach ($d in $debDepends) { $lines.Add("      - $(Yaml $d)") }
    $lines.Add('    recommends:')
    $lines.Add("      - libsecret-1-0")
    $lines.Add('  rpm:')
    $lines.Add('    depends:')
    foreach ($d in $rpmDepends) { $lines.Add("      - $(Yaml $d)") }
    $lines.Add('    recommends:')
    $lines.Add("      - libsecret")

    Set-Content -Path $Path -Value $lines -Encoding utf8NoBOM
}

function Build-AppImage([string] $Rid, [string] $Publish, [string] $Tool) {
    $arch = if ($Rid -eq 'linux-arm64') { 'aarch64' } else { 'x86_64' }
    $appDir = Join-Path $work "$Rid/Orrery.AppDir"
    Remove-Item -Recurse -Force $appDir -ErrorAction SilentlyContinue
    $lib = Join-Path $appDir 'usr/lib/orrery'
    New-Item -ItemType Directory -Force $lib | Out-Null
    Copy-Item -Recurse (Join-Path $Publish '*') $lib
    foreach ($size in $iconSizes) {
        $dir = Join-Path $appDir "usr/share/icons/hicolor/${size}x${size}/apps"
        New-Item -ItemType Directory -Force $dir | Out-Null
        Copy-Item (Join-Path $linux "icons/$size.png") (Join-Path $dir 'orrery.png')
    }
    $doc = Join-Path $appDir 'usr/share/doc/orrery'
    New-Item -ItemType Directory -Force $doc | Out-Null
    Copy-Item (Join-Path $root 'LICENSE'), (Join-Path $Publish 'THIRD-PARTY-NOTICES.txt') $doc
    Copy-Item (Join-Path $linux 'AppRun'), (Join-Path $linux 'orrery.desktop') $appDir
    Copy-Item (Join-Path $linux 'icons/256.png') (Join-Path $appDir 'orrery.png')
    Copy-Item (Join-Path $linux 'icons/256.png') (Join-Path $appDir '.DirIcon')
    chmod +x (Join-Path $appDir 'AppRun') (Join-Path $lib 'Orrery') (Join-Path $lib 'createdump')
    if ($LASTEXITCODE -ne 0) { throw "chmod failed in $appDir." }

    $image = Join-Path $out "Orrery-$version-$arch.AppImage"
    $env:ARCH = $arch
    try { & $Tool --no-appstream $appDir $image }
    finally { Remove-Item Env:ARCH }
    if ($LASTEXITCODE -ne 0) { throw "appimagetool failed for $Rid." }
    Write-Host "   $image" -ForegroundColor Green
}

$wantNfpm = $Formats -contains 'deb' -or $Formats -contains 'rpm'
$wantAppImage = $Formats -contains 'appimage'
$nfpm = if ($wantNfpm) { Find-Tool $NfpmPath 'nfpm' 'Install it from https://nfpm.goreleaser.com/install/ or pass -NfpmPath.' }
if ($wantAppImage -and -not $IsLinux) {
    Write-Warning 'Skipping the AppImage: appimagetool only runs on Linux.'
    $wantAppImage = $false
}
$appImageTool = if ($wantAppImage) { Find-Tool $AppImageToolPath 'appimagetool' 'Get it from https://github.com/AppImage/appimagetool/releases or pass -AppImageToolPath.' }

New-Item -ItemType Directory -Force $out | Out-Null

foreach ($rid in $Runtimes) {
    Write-Host "== $rid" -ForegroundColor Cyan
    $publish = Join-Path $work "$rid/publish"
    Remove-Item -Recurse -Force $publish -ErrorAction SilentlyContinue
    dotnet publish $project -c $Configuration -r $rid --self-contained true -o $publish -p:DebugType=none -p:DebugSymbols=false -p:OrreryLockedRestore=true
    if ($LASTEXITCODE -ne 0) { throw "Publishing $rid failed." }
    & (Join-Path $PSScriptRoot 'Generate-ThirdPartyNotices.ps1') -PublishDir $publish
    # Installed copies must never find a portable marker beside the app.
    Remove-Item (Join-Path $publish 'orrery.portable.json') -ErrorAction SilentlyContinue

    if ($wantNfpm) {
        $config = Join-Path $work "$rid/nfpm.yaml"
        Write-NfpmConfig $rid $publish $config
        foreach ($format in @('deb', 'rpm') | Where-Object { $Formats -contains $_ }) {
            & $nfpm package --config $config --packager $format --target $out
            if ($LASTEXITCODE -ne 0) { throw "nfpm failed building the $format for $rid." }
        }
    }
    if ($wantAppImage) { Build-AppImage $rid $publish $appImageTool }
}

Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
Write-Host "Packages are in $out" -ForegroundColor Green
