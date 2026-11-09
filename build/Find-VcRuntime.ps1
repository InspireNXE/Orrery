<#
.SYNOPSIS
    Finds vcruntime140.dll for a Windows runtime identifier in Visual Studio's redistributable folder.

.DESCRIPTION
    libsodium.dll, which NSec loads for the vault, is built with MSVC and needs VCRUNTIME140.dll. Windows doesn't
    include it: a machine without the Visual C++ Redistributable (any fresh install) can't load libsodium, and
    Settings and the vault fail. Orrery ships Microsoft's redistributable copy app-locally beside it, as the
    Visual Studio license allows; the rest of the C runtime (api-ms-win-crt-*) is part of Windows 10 and later.

    Returns the newest copy for the architecture from the newest Visual Studio with the C++ build tools, or
    nothing if there isn't one.

.EXAMPLE
    ./build/Find-VcRuntime.ps1 -Runtime win-x64
#>
param([Parameter(Mandatory)] [string] $Runtime)

$arch = switch ($Runtime) { 'win-x64' { 'x64' } 'win-arm64' { 'arm64' } 'win-x86' { 'x86' } default { return } }
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) { return }

$found = foreach ($vs in (& $vswhere -products * -property installationPath)) {
    $redist = Join-Path $vs 'VC\Redist\MSVC'
    if (-not (Test-Path $redist)) { continue }
    Get-ChildItem $redist -Directory | ForEach-Object {
        Get-ChildItem (Join-Path $_.FullName $arch) -Directory -Filter 'Microsoft.VC14*.CRT' -ErrorAction SilentlyContinue |
            ForEach-Object { Get-Item (Join-Path $_.FullName 'vcruntime140.dll') -ErrorAction SilentlyContinue }
    }
}
# By the numeric parts: the version string can carry text ("14.29.30157.0 built by: cloudtest").
$found | Sort-Object { $v = $_.VersionInfo; [version]::new($v.FileMajorPart, $v.FileMinorPart, $v.FileBuildPart, $v.FilePrivatePart) } -Descending | Select-Object -First 1 | ForEach-Object FullName
