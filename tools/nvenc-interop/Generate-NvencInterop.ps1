<#
.SYNOPSIS
Regenerates src/Orrery.Host/Encoding/Nvenc/NvEncInterop.g.cs from NVIDIA's nvEncodeAPI.h.

.DESCRIPTION
Fetches nvEncodeAPI.h from FFmpeg's nv-codec-headers at the pinned tag, refuses it unless its SHA-256 matches, then
compiles generate.c against it with MSVC (found through vswhere) and writes the C# it prints. Nothing is installed; the
header and the compiled generator stay in a temporary folder. Change $Tag and $Sha256 together to move to another API
version, and check the result builds and probes on an NVIDIA machine.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

# NVENC API 12.1: drivers 531.61 and later on Windows, 530.41.03 and later on Linux; has AV1.
$Tag = 'n12.1.14.1'
$Sha256 = 'dbdaa8bcbc2b9325bd24bc68b36170a8432411518040e8d4c8421608cf217425'

$root = Resolve-Path (Join-Path $PSScriptRoot '../..')
$output = Join-Path $root 'src/Orrery.Host/Encoding/Nvenc/NvEncInterop.g.cs'
$work = Join-Path ([IO.Path]::GetTempPath()) "orrery-nvenc-$Tag"
New-Item -ItemType Directory -Force $work | Out-Null

$header = Join-Path $work 'nvEncodeAPI.h'
Invoke-WebRequest "https://raw.githubusercontent.com/FFmpeg/nv-codec-headers/$Tag/include/ffnvcodec/nvEncodeAPI.h" -OutFile $header
$actual = (Get-FileHash $header -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $Sha256) {
    throw "nvEncodeAPI.h at $Tag has SHA-256 $actual, expected $Sha256. Not generating."
}

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
cmd /c "`"$vcvars`" >nul && cl /nologo /W3 /I`"$work`" /Fe`"$exe`" /Fo`"$work\\`" `"$source`""
if ($LASTEXITCODE -ne 0) {
    throw 'The generator did not compile.'
}

$lines = & $exe "nv-codec-headers $Tag (nvEncodeAPI.h, SHA-256 $Sha256)"
if ($LASTEXITCODE -ne 0) {
    throw 'The generator failed.'
}

[IO.File]::WriteAllText($output, (($lines -join "`r`n") + "`r`n"), [Text.UTF8Encoding]::new($false))
Write-Host "Wrote $output"
