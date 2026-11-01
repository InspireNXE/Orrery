#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Puts the Devolutions.IronRdp package Orrery builds with into .nuget-local, where nuget.config's ironrdp-local
    source looks for it.

.DESCRIPTION
    Orrery uses a Devolutions.IronRdp built from a fork of IronRDP, which isn't on nuget.org. build/ironrdp-package.json
    says which one: its version (IronRdpVersion in Directory.Build.props), the fork and commit it was built from, and
    the URL and SHA-256 of the copy published as a GitHub release asset. This downloads that file into .nuget-local and
    checks its SHA-256 against the json, and refuses to put it there if they differ or the json has no hash yet.

    -FromFolder copies Devolutions.IronRdp.<version>.nupkg from a folder instead, such as a package you built yourself.
    It's checked against the json's hash too, or against -Sha256 when that's given. -SkipHashCheck takes it
    unchecked, with a warning.

    Afterwards it warns if NuGet's cache holds a different package of the same version (restores would keep using
    that one) or if the lock files record a different content hash (locked restores would fail with NU1403).

.EXAMPLE
    ./build/Get-IronRdp.ps1                                                  # download the published package
    ./build/Get-IronRdp.ps1 -FromFolder ../IronRDP/dependencies/nuget -Sha256 <SHA-256 of your package>
    ./build/Get-IronRdp.ps1 -FromFolder ../IronRDP/dependencies/nuget -SkipHashCheck
#>
param(
    [string] $FromFolder,
    [string] $Sha256,
    [string] $Url,
    [switch] $SkipHashCheck
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$packageJson = Join-Path $PSScriptRoot 'ironrdp-package.json'
$package = Get-Content -Raw $packageJson | ConvertFrom-Json
$version = $package.version
$wanted = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.IronRdpVersion | Where-Object { $_ } | Select-Object -First 1
if ($version -ne $wanted) {
    throw "build/ironrdp-package.json is for Devolutions.IronRdp $version, but Directory.Build.props uses $wanted. Publish $wanted and commit its ironrdp-package.json (docs/packaging.md, ""A new IronRDP package""), or pass -FromFolder with a package you built."
}

if ($SkipHashCheck -and -not $FromFolder) { throw '-SkipHashCheck only goes with -FromFolder. A download is always checked.' }
$expected = if ($Sha256) { $Sha256 } else { $package.sha256 }
$expected = "$expected".Trim().ToLowerInvariant()
if (-not $SkipHashCheck) {
    if (-not $expected) {
        throw "build/ironrdp-package.json has no sha256 yet, so Devolutions.IronRdp $version hasn't been published. With a package you built, pass -FromFolder <folder> -Sha256 <its SHA-256>."
    }
    if ($expected -notmatch '^[0-9a-f]{64}$') { throw "'$expected' isn't a SHA-256 (64 hex digits)." }
}

$fileName = "Devolutions.IronRdp.$version.nupkg"
$localSource = Join-Path $root '.nuget-local'
$target = Join-Path $localSource $fileName
# NuGet only looks at .nupkg files, so a half-downloaded or rejected file here is never restored.
$partial = "$target.part"

function Get-Sha256([string] $Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }

New-Item -ItemType Directory -Force $localSource | Out-Null
Remove-Item -LiteralPath $partial -Force -ErrorAction SilentlyContinue

if (-not $FromFolder -and $expected -and (Test-Path -LiteralPath $target -PathType Leaf) -and (Get-Sha256 $target) -eq $expected) {
    Write-Host "$fileName is already in .nuget-local."
}
else {
    if ($FromFolder) {
        $source = Join-Path $FromFolder $fileName
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "$source doesn't exist." }
        Write-Host "Copying $source"
        Copy-Item -LiteralPath $source $partial
    }
    else {
        $from = if ($Url) { $Url } else { $package.url }
        if (-not $from) { throw 'build/ironrdp-package.json has no url yet. Pass -Url, or -FromFolder with a package you built.' }
        if ($from -notmatch '^https://') { throw "Only https downloads are allowed, not $from." }
        Write-Host "Downloading $from"
        # Windows PowerShell 5.1 may leave TLS 1.2 off, and its progress bar slows a large download right down.
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        $ProgressPreference = 'SilentlyContinue'
        Invoke-WebRequest -Uri $from -OutFile $partial -UseBasicParsing
    }

    $actual = Get-Sha256 $partial
    if ($SkipHashCheck) {
        Write-Warning "Not checking $fileName against a known SHA-256 (-SkipHashCheck). Its SHA-256 is $actual."
    }
    elseif ($actual -ne $expected) {
        Remove-Item -LiteralPath $partial -Force
        throw "$fileName has SHA-256 $actual, not the expected $expected. It wasn't put in .nuget-local."
    }
    Move-Item -LiteralPath $partial $target -Force
    Write-Host "$fileName is in .nuget-local (SHA-256 $actual)." -ForegroundColor Green
}

# NuGet's cache keeps the base64 SHA-512 of each .nupkg beside it. For a package that isn't signed, as builds from
# the fork aren't, the lock files' content hash is the same value.
$stream = [IO.File]::OpenRead($target)
$sha512 = [Security.Cryptography.SHA512]::Create()
try { $contentHash = [Convert]::ToBase64String($sha512.ComputeHash($stream)) } finally { $sha512.Dispose(); $stream.Dispose() }

$globalPackages = if ($Env:NUGET_PACKAGES) { $Env:NUGET_PACKAGES } else { Join-Path (Join-Path $HOME '.nuget') 'packages' }
$cached = Join-Path (Join-Path $globalPackages 'devolutions.ironrdp') $version.ToLowerInvariant()
$cachedHash = Join-Path $cached "devolutions.ironrdp.$($version.ToLowerInvariant()).nupkg.sha512"
if ((Test-Path -LiteralPath $cachedHash) -and (Get-Content -Raw $cachedHash).Trim() -ne $contentHash) {
    Write-Warning "NuGet's cache holds a different Devolutions.IronRdp $version, which restores would keep using. Delete $cached, then restore again."
}

$lockFile = Join-Path $root 'src/Orrery.Protocols.Rdp/packages.lock.json'
if ((Get-Content -Raw $lockFile) -match '"Devolutions\.IronRdp": \{[^{}]*?"contentHash": "([^"]*)"' -and $Matches[1] -ne $contentHash) {
    Write-Warning "The lock files record a different Devolutions.IronRdp, so a locked restore fails with NU1403. If this is the package everyone should use, run dotnet restore Orrery.sln --force-evaluate and commit the lock files (docs/packaging.md)."
}
