#Requires -Version 5.1
<#
.SYNOPSIS
    Builds Orrery Setup (artifacts\installer\Orrery-Setup-<version>.exe) with Inno Setup, the same way Prober's
    desktop setup is built.

.DESCRIPTION
    Publishes Orrery for Windows (self-contained, so no .NET install is needed), generates
    THIRD-PARTY-NOTICES.txt into the publish output, and compiles installer\Orrery.iss with ISCC.exe. The version
    comes from <Version> in Directory.Build.props.

    Needs Inno Setup 7 installed per user (%LOCALAPPDATA%\Programs\Inno Setup 7\ISCC.exe) or Inno Setup 6 under
    either Program Files folder; pass -IsccPath for anywhere else.

.EXAMPLE
    build\Build-Installer.ps1
    build\Build-Installer.ps1 -PublishDir artifacts\installer-publish -SkipPublish   # iterate on the .iss
#>
[CmdletBinding()]
param(
    [string]$PublishDir,
    [switch]$SkipPublish,
    [string]$OutputDir,
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$IsccPath
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path $PSScriptRoot -Parent

function Resolve-IsccPath([string]$Explicit) {
    if (-not [string]::IsNullOrWhiteSpace($Explicit)) {
        if (-not (Test-Path $Explicit)) { throw "-IsccPath '$Explicit' does not exist." }
        return $Explicit
    }

    $candidates = @(
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 7\ISCC.exe"),
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
        "C:\Program Files\Inno Setup 6\ISCC.exe"
    )
    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) { return $candidate }
    }

    $onPath = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    throw "Could not find ISCC.exe. Pass -IsccPath, or install Inno Setup 7 to its default per-user location."
}

function Invoke-Native([string]$FilePath, [string[]]$Arguments, [string]$Description) {
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Description failed with exit code $LASTEXITCODE." }
}

$version = ([xml](Get-Content (Join-Path $repoRoot "Directory.Build.props"))).Project.PropertyGroup.Version |
    Where-Object { $_ } | Select-Object -First 1
Write-Host "Version: $version"

if ([string]::IsNullOrWhiteSpace($PublishDir)) { $PublishDir = Join-Path $repoRoot "artifacts\installer-publish" }
if ($SkipPublish) {
    $PublishDir = (Resolve-Path $PublishDir).Path
    Write-Host "Skipping publish and using the existing output at '$PublishDir'."
}
else {
    # Emptied first, so nothing from an earlier build ends up in the setup.
    if (Test-Path $PublishDir) { Remove-Item -Recurse -Force $PublishDir }
    Write-Host "Publishing Orrery ($Runtime, self-contained) to '$PublishDir'..."
    Invoke-Native "dotnet" -Description "dotnet publish" -Arguments @(
        "publish", (Join-Path $repoRoot "src\Orrery.App\Orrery.App.csproj"),
        "-c", $Configuration, "-r", $Runtime, "--self-contained", "true",
        "-p:DebugType=none", "-p:DebugSymbols=false", "-o", $PublishDir)
    $PublishDir = (Resolve-Path $PublishDir).Path
}

if (-not (Test-Path (Join-Path $PublishDir "Orrery.exe"))) {
    throw "'$PublishDir' has no Orrery.exe, so it doesn't look like an Orrery publish output."
}

& (Join-Path $PSScriptRoot "Generate-ThirdPartyNotices.ps1") -PublishDir $PublishDir

if ([string]::IsNullOrWhiteSpace($OutputDir)) { $OutputDir = Join-Path $repoRoot "artifacts\installer" }
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$OutputDir = (Resolve-Path $OutputDir).Path
Get-ChildItem $OutputDir -Filter "Orrery-Setup-*.exe" -ErrorAction SilentlyContinue | Remove-Item -Force

$iscc = Resolve-IsccPath $IsccPath
Write-Host ""
Write-Host "Compiling installer\Orrery.iss with ISCC.exe ('$iscc')..."
Invoke-Native $iscc -Description "ISCC.exe" -Arguments @(
    "/DAppVersion=$version", "/DPublishDir=$PublishDir", "/DOutputDir=$OutputDir",
    (Join-Path $repoRoot "installer\Orrery.iss"))

$setup = Join-Path $OutputDir "Orrery-Setup-$version.exe"
if (-not (Test-Path $setup)) { throw "Expected '$setup' but it wasn't found. Check OutputBaseFilename in Orrery.iss." }
Write-Host ""
Write-Host ("Built '$setup' ({0:N1} MB)." -f ((Get-Item $setup).Length / 1MB))
