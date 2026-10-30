#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Writes THIRD-PARTY-NOTICES.txt from what a publish of Orrery actually ships.

.DESCRIPTION
    Reads <PublishDir>/Orrery.deps.json for the packages and .NET runtime pack in the build, then for each one
    copies the licence and notice files the package carries, unchanged. A package that only names its licence
    (a SPDX expression such as MIT) gets that licence's standard text with the package's copyright line. Texts
    for packages that carry none, or that bundle something under another licence, live in build/licenses/<id>.txt
    with their source named. Identical texts are printed once, under every package that shares them.

    Fails when a shipped package has no licence it can find, so nothing ships unacknowledged.

.EXAMPLE
    ./build/Generate-ThirdPartyNotices.ps1 -PublishDir artifacts/installer-publish
#>
param(
    [Parameter(Mandatory)] [string] $PublishDir,
    [string] $OutputFile
)

# Written into the publish output by default, so each platform's build carries the notices for what it ships.

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $OutputFile) { $OutputFile = Join-Path $PublishDir 'THIRD-PARTY-NOTICES.txt' }
$packages = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages' }
$extras = Join-Path $PSScriptRoot 'licenses'

$depsFile = Get-ChildItem $PublishDir -Filter '*.deps.json' | Select-Object -First 1
if (-not $depsFile) { throw "No .deps.json in '$PublishDir'; pass a dotnet publish output." }
$deps = Get-Content $depsFile.FullName -Raw | ConvertFrom-Json

$mit = @'
Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
documentation files (the "Software"), to deal in the Software without restriction, including without limitation the
rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit
persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the
Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
'@

# The texts to print, in order, each with the packages that share it.
$texts = [ordered]@{}
function Add-Text([string] $Text, [string] $Package) {
    $key = ($Text -replace "`r`n", "`n").Trim()
    if (-not $texts.Contains($key)) { $texts[$key] = [System.Collections.Generic.List[string]]::new() }
    if (-not $texts[$key].Contains($Package)) { $texts[$key].Add($Package) }
}

$listed = [System.Collections.Generic.List[string]]::new()
foreach ($library in $deps.libraries.PSObject.Properties) {
    if ($library.Value.type -notin 'package', 'runtimepack') { continue }
    $id, $version = $library.Name -split '/', 2
    $id = $id -replace '^runtimepack\.', ''
    $dir = Join-Path $packages "$($id.ToLowerInvariant())/$version"
    if (-not (Test-Path $dir)) { throw "Package folder '$dir' is missing; restore first." }

    $nuspec = [xml](Get-Content (Get-ChildItem $dir -Filter '*.nuspec' | Select-Object -First 1).FullName -Raw)
    $meta = $nuspec.package.metadata
    $expression = if ($meta.license.type -eq 'expression') { $meta.license.'#text' } elseif ($meta.license.type -eq 'file') { 'see licence text' } else { $null }
    $copyright = [string]$meta.copyright
    $label = "$id $version"
    $found = $false

    foreach ($file in Get-ChildItem $dir -File | Where-Object Name -Match '^(licen[cs]e|notice|third-party-notices|copying)(\.(txt|md))?$' | Sort-Object Name) {
        Add-Text (Get-Content $file.FullName -Raw) $label
        $found = $true
    }

    if (-not $found -and $expression -eq 'MIT') {
        Add-Text "$($copyright.Trim())`n`n$mit" $label
        $found = $true
    }

    # Added to the package's own licence: a text it lacks, or something it bundles under another licence.
    $extra = Join-Path $extras "$id.txt"
    if (Test-Path $extra) {
        Add-Text (Get-Content $extra -Raw) $label
        $found = $true
    }

    if (-not $found) {
        throw "$label ships with no licence text and its licence ('$expression') has no standard text here. Add build/licenses/$id.txt."
    }

    $listed.Add("- $label$(if ($expression) { " ($expression)" })")
}

$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('Orrery third-party notices')
$lines.Add('==========================')
$lines.Add('')
$lines.Add('Orrery is self-contained: the .NET runtime and every library below ship beside it. This file is generated from the')
$lines.Add('build by build/Generate-ThirdPartyNotices.ps1. Each licence or notice text is copied unchanged from its package;')
$lines.Add('where a package only names its licence, the standard text follows its copyright line, and where a package carries')
$lines.Add('no text (or bundles something licensed separately), the section names where the text was copied from.')
$lines.Add('')
$lines.Add('Components')
$lines.Add('----------')
$lines.AddRange($listed)
foreach ($entry in $texts.GetEnumerator()) {
    $lines.Add('')
    $lines.Add('=' * 100)
    $lines.Add("Applies to: $($entry.Value -join ', ')")
    $lines.Add('=' * 100)
    $lines.Add('')
    $lines.AddRange([string[]]($entry.Key -split "`n"))
}

[System.IO.File]::WriteAllLines($OutputFile, $lines, [System.Text.UTF8Encoding]::new($false))
Write-Host "Wrote ${OutputFile}: $($listed.Count) components, $($texts.Count) licence and notice texts."
