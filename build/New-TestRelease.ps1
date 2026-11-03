#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Builds a release on this machine, for trying Orrery's updates before a real release exists.

.DESCRIPTION
    Writes artifacts/test-release/: the portable zips for -Runtimes (and with -Installer, Orrery Setup), their
    SHA256SUMS, and releases.json, a release list in the shape GitHub's API returns, naming the files by relative path.
    Point ORRERY_UPDATE_FEED at releases.json and an older Orrery reads it instead of GitHub, then downloads, checks and
    applies the update from these files. See docs/packaging.md, "Testing updates".

    -Prerelease marks the release a pre-release, which Orrery offers only with previews turned on (a version with a
    "-", like 0.2.0-preview.1, counts as one anyway). -BreakChecksum writes a wrong SHA256SUMS line for every
    package and leaves out GitHub's digest, so the download fails its check: for trying the failure path.

    -Installer needs Windows and Inno Setup 7 (see build/Build-Installer.ps1). -AppImage needs Linux and appimagetool
    (see build/Build-LinuxPackages.ps1).

.EXAMPLE
    ./build/New-TestRelease.ps1                              # the next patch version, for this OS's portable zip
    ./build/New-TestRelease.ps1 -Version 0.1.1 -Installer    # and Orrery Setup
    ./build/New-TestRelease.ps1 -Version 0.2.0-preview.1 -BreakChecksum
#>
param(
    [string] $Version,
    [string[]] $Runtimes,
    [switch] $Installer,
    [switch] $AppImage,
    [switch] $Prerelease,
    [switch] $BreakChecksum
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $root 'artifacts'
$out = Join-Path $artifacts 'test-release'
$current = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

# By default, the next patch version after the one in Directory.Build.props, so a build of that is older.
if (-not $Version) {
    $parts = ($current -split '-')[0] -split '\.'
    $Version = "$($parts[0]).$($parts[1]).$([int]$parts[2] + 1)"
}
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw "'$Version' isn't a version like 1.2.3 or 1.2.3-preview.1." }

# Windows PowerShell 5.1 has no $IsLinux or $IsMacOS: there they're null, so Windows.
$os = if ($IsLinux) { 'linux' } elseif ($IsMacOS) { 'osx' } else { 'win' }
$arch = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'arm64' } else { 'x64' }
if (-not $Runtimes) { $Runtimes = @("$os-$arch") }

if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Force $out | Out-Null

Write-Host "Test release $Version for $($Runtimes -join ', ')" -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'package.ps1') -Runtimes $Runtimes -Version $Version
foreach ($rid in $Runtimes) {
    $name = "Orrery-$Version-$($rid -replace '^osx-', 'macos-')-portable"
    Move-Item (Join-Path $artifacts "$name.zip") $out
    Remove-Item -Recurse -Force (Join-Path $artifacts $name) -ErrorAction SilentlyContinue
}

if ($Installer) {
    & (Join-Path $PSScriptRoot 'Build-Installer.ps1') -Version $Version -OutputDir $out
}

if ($AppImage) {
    $linuxRuntimes = @($Runtimes | Where-Object { $_ -like 'linux-*' })
    if (-not $linuxRuntimes) { $linuxRuntimes = @("linux-$arch") }
    & (Join-Path $PSScriptRoot 'Build-LinuxPackages.ps1') -Runtimes $linuxRuntimes -Formats appimage -Version $Version
    Get-ChildItem (Join-Path $artifacts 'linux') -Filter "Orrery-$Version-*.AppImage" | Move-Item -Destination $out
}

# SHA256SUMS as sha256sum writes it, and the release list naming each file relative to it.
$packages = @(Get-ChildItem $out -File | Sort-Object Name)
$sums = [System.Collections.Generic.List[string]]::new()
$assets = [System.Collections.Generic.List[object]]::new()
foreach ($package in $packages) {
    $hash = (Get-FileHash $package.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $listed = if ($BreakChecksum) { 'f' * 64 } else { $hash }
    $sums.Add("$listed  $($package.Name)")
    $asset = [ordered]@{ name = $package.Name; size = $package.Length; browser_download_url = $package.Name }
    if (-not $BreakChecksum) { $asset.digest = "sha256:$hash" }
    $assets.Add($asset)
}

$sumsPath = Join-Path $out 'SHA256SUMS'
# LF line endings and UTF-8 without a BOM, as on GitHub, in Windows PowerShell 5.1 too.
[IO.File]::WriteAllText($sumsPath, (($sums -join "`n") + "`n"), [Text.UTF8Encoding]::new($false))
$assets.Add([ordered]@{ name = 'SHA256SUMS'; size = (Get-Item $sumsPath).Length; browser_download_url = 'SHA256SUMS' })

$release = [ordered]@{
    tag_name = "v$Version"
    name = "Orrery $Version (test release)"
    body = "A local test release made by build/New-TestRelease.ps1."
    html_url = 'https://github.com/InspireNXE/Orrery/releases'
    draft = $false
    prerelease = [bool]$Prerelease -or $Version.Contains('-')
    published_at = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
    assets = $assets.ToArray()
}
$feed = Join-Path $out 'releases.json'
[IO.File]::WriteAllText($feed, (ConvertTo-Json -InputObject @($release) -Depth 5), [Text.UTF8Encoding]::new($false))

Write-Host ''
Write-Host "Test release $Version is in $out" -ForegroundColor Green
foreach ($line in $sums) { Write-Host "   $line" }
if ($BreakChecksum) { Write-Host "   (wrong on purpose: the download should fail its check)" -ForegroundColor Yellow }

$oldRid = $Runtimes[0]
$oldZip = Join-Path $artifacts "Orrery-$current-$($oldRid -replace '^osx-', 'macos-')-portable.zip"
$exe = if ($oldRid -like 'win-*') { 'Orrery.exe' } else { 'Orrery' }
Write-Host ''
Write-Host 'To try it:' -ForegroundColor Cyan
Write-Host "  1. Build an older copy (Directory.Build.props says $current; it must be older than $Version):"
Write-Host "       ./build/package.ps1 -Runtimes $oldRid"
Write-Host "  2. Unzip $oldZip into an empty folder of its own (not under artifacts, which the next build clears)."
if ($oldRid -notlike 'win-*') { Write-Host "     Then: chmod +x Orrery" }
Write-Host '  3. In a shell, point Orrery at this release and start the older copy from that shell:'
if ($os -eq 'win') {
    Write-Host "       `$env:ORRERY_UPDATE_FEED = '$feed'"
    Write-Host "       & '<that folder>\$exe'"
}
else {
    Write-Host "       export ORRERY_UPDATE_FEED='$feed'"
    Write-Host "       '<that folder>/$exe'"
}
Write-Host '  4. Check for updates in Orrery. It should offer this release, download and check it, and restart as the new version.'
if ($Installer) {
    Write-Host ''
    Write-Host '  For the installed copy: build and run the older setup (build\Build-Installer.ps1), then set the variable for'
    Write-Host '  your user, start Orrery from the Start menu, and remove the variable when done:'
    Write-Host "       [Environment]::SetEnvironmentVariable('ORRERY_UPDATE_FEED', '$feed', 'User')"
    Write-Host "       [Environment]::SetEnvironmentVariable('ORRERY_UPDATE_FEED', `$null, 'User')"
}
