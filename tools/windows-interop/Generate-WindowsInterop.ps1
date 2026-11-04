<#
.SYNOPSIS
Regenerates src/Orrery.Protocols.Stream/Windows/WindowsInterop.g.cs: the vtable slots, GUIDs, constants and struct
layouts the OSP client's Media Foundation decoder and D3D11 presenter call, read from the Windows SDK's headers by
compiling generate.c with MSVC. Nothing is downloaded; the SDK and the C++ x64 build tools must be installed.
#>
$ErrorActionPreference = 'Stop'

$root = Resolve-Path (Join-Path $PSScriptRoot '../..')
$output = Join-Path $root 'src/Orrery.Protocols.Stream/Windows/WindowsInterop.g.cs'
$work = Join-Path ([IO.Path]::GetTempPath()) 'orrery-windows-interop'
New-Item -ItemType Directory -Force $work | Out-Null

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) {
    throw 'MSVC (the C++ x64 build tools) is needed to compile the generator.'
}

$vcvars = Join-Path $vs 'VC/Auxiliary/Build/vcvars64.bat'
# vcvars64.bat looks for vswhere on PATH and complains when it isn't there.
$env:PATH = "$(Split-Path $vswhere);$env:PATH"
$exe = Join-Path $work 'generate.exe'
$source = Join-Path $PSScriptRoot 'generate.c'
# /Tc: the source is compiled as C, since the headers only declare their C vtable structs then. The libraries hold
# the GUIDs' values.
cmd /c "`"$vcvars`" >nul && cl /nologo /W3 /Fe`"$exe`" /Fo`"$work\\`" /Tc`"$source`" mfuuid.lib dxguid.lib"
if ($LASTEXITCODE -ne 0) {
    throw 'The generator did not compile.'
}

$sdk = (Get-ChildItem "${env:ProgramFiles(x86)}/Windows Kits/10/Include" -Directory | Sort-Object { [version]$_.Name } | Select-Object -Last 1).Name
$lines = & $exe "the Windows SDK's headers ($sdk)"
if ($LASTEXITCODE -ne 0) {
    throw 'The generator failed.'
}

[IO.File]::WriteAllText($output, (($lines -join "`r`n") + "`r`n"), [Text.UTF8Encoding]::new($false))
Write-Host "Wrote $output"
