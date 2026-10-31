#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Builds every Orrery package this machine can: the portable zips, the Windows installer and the Linux packages.

.DESCRIPTION
    Runs, in order:
      build/package.ps1              portable zips for win-x64, win-arm64, linux-x64, linux-arm64, osx-arm64, osx-x64
      build/Build-Installer.ps1      Windows installer (Windows only; needs Inno Setup 7)
      build/Build-LinuxPackages.ps1  .deb and .rpm (needs nfpm), AppImage (needs appimagetool, Linux only)

    A step whose tool is missing is skipped with a warning instead of stopping the rest, and the summary at the end
    says what was built and what wasn't. Everything lands in artifacts/.

.EXAMPLE
    ./build/Build-All.ps1
    ./build/Build-All.ps1 -NfpmPath C:\tools\nfpm.exe -SkipInstaller
#>
param(
    [switch] $SkipPortable,
    [switch] $SkipInstaller,
    [switch] $SkipLinux,
    [string] $IsccPath,
    [string] $NfpmPath,
    [string] $AppImageToolPath
)

$ErrorActionPreference = 'Stop'
$skipped = [System.Collections.Generic.List[string]]::new()
$built = [System.Collections.Generic.List[string]]::new()

if (-not $SkipPortable) {
    & (Join-Path $PSScriptRoot 'package.ps1')
    $built.Add('portable zips (artifacts/)')
}

if ($SkipInstaller) { }
elseif (-not $IsWindows) { $skipped.Add('Windows installer: Inno Setup only runs on Windows') }
else {
    $installerArgs = @{}
    if ($IsccPath) { $installerArgs.IsccPath = $IsccPath }
    try {
        & (Join-Path $PSScriptRoot 'Build-Installer.ps1') @installerArgs
        $built.Add('Windows installer (artifacts/installer/)')
    }
    catch [System.Management.Automation.RuntimeException] {
        # Only a missing Inno Setup is skipped; a wrong -IsccPath or a failed build still stops here.
        if ($_.Exception.Message -notlike 'Could not find ISCC.exe*') { throw }
        $skipped.Add("Windows installer: $($_.Exception.Message)")
    }
}

if (-not $SkipLinux) {
    $formats = @()
    if ($NfpmPath -or (Get-Command nfpm -ErrorAction SilentlyContinue)) { $formats += 'deb', 'rpm' }
    else { $skipped.Add('.deb and .rpm: nfpm not found (https://nfpm.goreleaser.com/install/, or pass -NfpmPath)') }
    if (-not $IsLinux) { $skipped.Add('AppImage: appimagetool only runs on Linux') }
    elseif ($AppImageToolPath -or (Get-Command appimagetool -ErrorAction SilentlyContinue)) { $formats += 'appimage' }
    else { $skipped.Add('AppImage: appimagetool not found (or pass -AppImageToolPath)') }

    if ($formats) {
        $linuxArgs = @{ Formats = $formats }
        if ($NfpmPath) { $linuxArgs.NfpmPath = $NfpmPath }
        if ($AppImageToolPath) { $linuxArgs.AppImageToolPath = $AppImageToolPath }
        & (Join-Path $PSScriptRoot 'Build-LinuxPackages.ps1') @linuxArgs
        $built.Add("Linux $($formats -join ', ') (artifacts/linux/)")
    }
}

Write-Host ''
foreach ($b in $built) { Write-Host "Built:   $b" -ForegroundColor Green }
foreach ($s in $skipped) { Write-Host "Skipped: $s" -ForegroundColor Yellow }
