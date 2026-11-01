#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Refreshes Devolutions.IronRdp's content hash in every packages.lock.json for an IronRDP package built on another
    machine, and fails if anything else in the lock files changed.

.DESCRIPTION
    The lock files record the content hash of the Devolutions.IronRdp package built on the machine that last updated
    them. A package built from the same IronRDP commit anywhere else (GitHub Actions builds its own) has a different
    hash, so a locked restore fails with NU1403. This runs `dotnet restore Orrery.sln --force-evaluate` (not locked),
    then compares each lock file with the committed one with Devolutions.IronRdp's contentHash blanked out on both
    sides: they must match exactly, so every other package, and IronRDP's version, stays as locked. A lock file that
    git doesn't track yet fails too.

    Run it after pointing nuget.config's ironrdp-local source at the folder holding the new package. Afterwards the
    packaging scripts' locked restores (-p:OrreryLockedRestore=true) accept that package.

.EXAMPLE
    ./build/Update-IronRdpLockHashes.ps1               # restore, then compare
    ./build/Update-IronRdpLockHashes.ps1 -NoRestore    # just compare, after a restore you ran yourself
#>
param(
    [switch] $NoRestore
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

if (-not $NoRestore) {
    # RestoreLockedMode=false: the release-build properties would otherwise make this restore locked too.
    dotnet restore (Join-Path $root 'Orrery.sln') --force-evaluate --disable-build-servers -p:RestoreLockedMode=false
    if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }
}

# The hash is the only line of the entry that may differ; [^{}] keeps the match inside one entry. Line endings are
# normalized because a Windows checkout's files and git's copies differ there.
$hashPattern = '("Devolutions\.IronRdp": \{[^{}]*?"contentHash": ")[^"]*(")'
function Normalize([string] $Text) { ($Text -replace "`r`n", "`n").TrimEnd() -replace $hashPattern, '$1$2' }

$untracked = @(git -C $root ls-files --others --exclude-standard -- '*packages.lock.json')
if ($LASTEXITCODE -ne 0) { throw 'git ls-files failed.' }
if ($untracked.Count -gt 0) { throw "Lock files that aren't committed: $($untracked -join ', '). Commit them first." }

$changed = @(git -C $root diff --name-only HEAD -- '*packages.lock.json')
if ($LASTEXITCODE -ne 0) { throw 'git diff failed.' }

$drifted = @()
foreach ($path in $changed) {
    $committed = (git -C $root show "HEAD:$path") -join "`n"
    if ($LASTEXITCODE -ne 0) { throw "git show HEAD:$path failed." }
    $current = Get-Content -Raw (Join-Path $root $path)
    if ((Normalize $committed) -ne (Normalize $current)) { $drifted += $path }
    else { Write-Host "   $path (Devolutions.IronRdp content hash only)" }
}

if ($drifted.Count -gt 0) {
    git -C $root --no-pager diff HEAD -- $drifted
    throw "These lock files changed beyond Devolutions.IronRdp's content hash: $($drifted -join ', '). Update them with dotnet restore Orrery.sln --force-evaluate and commit them."
}
Write-Host "Lock files match the committed ones apart from Devolutions.IronRdp's content hash ($($changed.Count) refreshed)." -ForegroundColor Green
