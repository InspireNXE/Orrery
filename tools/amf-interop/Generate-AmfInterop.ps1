<#
.SYNOPSIS
Regenerates src/Orrery.Host/Encoding/Amf/AmfInterop.g.cs from AMD's AMF headers.

.DESCRIPTION
Fetches the AMF public headers the generator needs from GPUOpen-LibrariesAndSDKs/AMF at the pinned tag, refuses them
unless every file's SHA-256 matches, then compiles generate.c against them with MSVC (found through vswhere) and writes
the C# it prints. Nothing is installed; the headers and the compiled generator stay in a temporary folder. To move to
another AMF version, change $Tag and every hash together, and check the probe still encodes on an AMD machine.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

# AMF 1.4.36 (2025-04-29): older drivers' runtimes accept it, and it has the AV1 encoder.
$Tag = 'v1.4.36'
$Headers = [ordered]@{
    'components/ColorSpace.h'       = '2f6887d14516ebdcf034d59e39235f5d9974533ddb0b04d685c0c3b6d397d3d6'
    'components/Component.h'        = 'f3f18680d55a65fdf5e0351d3fde1dcd9bdd2d6caec268c8c915c4227ffa1edc'
    'components/ComponentCaps.h'    = '2479eb2a6174a1b63552c509abac1b701eddcab5a5eac5a7f4d9f40274d588b4'
    'components/PreAnalysis.h'      = '6824fd31dc6a42ad4e1cce29bd2bf190e769d6d5a4427fba57cefa697c9a9bd7'
    'components/VideoEncoderAV1.h'  = '4e31ddbe9ac074075c6d5aa7501f1d66082c71b1213ebd5fd36f14e068b461d1'
    'components/VideoEncoderHEVC.h' = '3d32657bc7b2df1b0e5449dca47115a13db5264c57cf18db6d547c1a789abf3d'
    'components/VideoEncoderVCE.h'  = 'f094a9b1ec77a0be1bc8f0012a767e916418899b66e33b6e3f08efb31e87d404'
    'core/AudioBuffer.h'            = 'c9382c911fd3b5e12105c1d2a67eb11e62e9475068825222a285bb1314ac0ddd'
    'core/Buffer.h'                 = '1ad18d3c813c3f81a836d393bbe86dfc0b7a1d7f04a77a272eb1461ce85ae3a4'
    'core/Compute.h'                = 'ef5721466fe48e1c038151018699519761e6e351e66e76a5de65386924653e20'
    'core/ComputeFactory.h'         = '50acf69fb670bc6cf093384d1b698bf9e44cda8dd49e02ef24eacfb86ad8ccae'
    'core/Context.h'                = 'f9b4acb784038aad234ddec490e5f6ad6dacc4b14794eb84f3523d56dc3d6dd2'
    'core/CurrentTime.h'            = '76d16ba57d48ae9e457f3d904698a1e7c625007ac8887db9bcecdb90fd36d835'
    'core/D3D12AMF.h'               = 'f05771305598b534e1efa0dd28825267ea87418c477b198e373f6764d060c666'
    'core/Data.h'                   = 'eaac4d80dd3215638c70f8a4ca01791d26d9df77586f665a772bb51907a8035b'
    'core/Debug.h'                  = '56666589bf6ad20c6286a048da751c2d7722f70c73d962ee3c205a6d709b6aff'
    'core/Dump.h'                   = 'a5e0fa9290cae767a4d135962ee5e30302e65e8d0ca5e016c620e72b759c86c6'
    'core/Factory.h'                = '644814e97dd359f08d66a73748b60262149b106d28423c31b573cecb4be57979'
    'core/Interface.h'              = '304f6d6077e9b0733859d44a5e43a343ae374c8fe72c7d79741c0aca89c21162'
    'core/Plane.h'                  = '5ad5165e9de2afcfc2da828e8704c838b1f82e8ead7422fbcfc32c758672e1f3'
    'core/Platform.h'               = '91f84c9fbd8aaf43909af373acff8e03c14406e44ac20217f0d115da7e09d7e1'
    'core/PropertyStorage.h'        = '0059b787e78cd7c4e57212ccaab56ad7e17e9474a31337da0eac5b2d056a5dd8'
    'core/PropertyStorageEx.h'      = '871136e46586baa1fc77121935b503aaa16523d006c3e435ce50698605675fad'
    'core/Result.h'                 = 'a469ff33875484b5762199da757819056ff4a7535bfdcec1f4cf2ffc403b98f2'
    'core/Surface.h'                = '7f0c99bbfe156169440031f75b0e37c28c435dd140ecb4e5089d0b1f01912c47'
    'core/Trace.h'                  = 'ae8c5fee671f02b90d3ccab312cfa6f74424351f070a7dd4ed7aba437fd55c30'
    'core/Variant.h'                = '72841e751595a2a8f62c8ac4a3e630b181411af8e0741d716bbe3379c9bf9a99'
    'core/Version.h'                = '09153735e3559a7d279c06f6d202f4baf3e6848c5f50a78dd01ea70a668ca834'
    'core/VulkanAMF.h'              = '385b8ad96b59e2d67a6f7a78c6aa660a2f2cdad1bf8c7cadcd83387a3ad4996c'
}

$root = Resolve-Path (Join-Path $PSScriptRoot '../..')
$output = Join-Path $root 'src/Orrery.Host/Encoding/Amf/AmfInterop.g.cs'
$work = Join-Path ([IO.Path]::GetTempPath()) "orrery-amf-$Tag"

foreach ($header in $Headers.GetEnumerator()) {
    $path = Join-Path $work $header.Key
    New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
    Invoke-WebRequest "https://raw.githubusercontent.com/GPUOpen-LibrariesAndSDKs/AMF/$Tag/amf/public/include/$($header.Key)" -OutFile $path
    $actual = (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $header.Value) {
        throw "$($header.Key) at $Tag has SHA-256 $actual, expected $($header.Value). Not generating."
    }
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
# /TC: the headers' C vtable structs are only declared when compiled as C.
cmd /c "`"$vcvars`" >nul && cl /nologo /W3 /TC /I`"$work`" /Fe`"$exe`" /Fo`"$work\\`" `"$source`""
if ($LASTEXITCODE -ne 0) {
    throw 'The generator did not compile.'
}

$lines = & $exe "GPUOpen AMF $Tag (amf/public/include, SHA-256 pinned per file in Generate-AmfInterop.ps1)"
if ($LASTEXITCODE -ne 0) {
    throw 'The generator failed.'
}

[IO.File]::WriteAllText($output, (($lines -join "`r`n") + "`r`n"), [Text.UTF8Encoding]::new($false))
Write-Host "Wrote $output"
