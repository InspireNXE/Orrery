using System.Runtime.CompilerServices;
using Orrery.Host.Encoding.Nvenc;
using Xunit;

namespace Orrery.Host.Tests;

public class NvEncTests
{
    [Fact]
    public void The_required_driver_version_is_the_generated_api_version()
    {
        // NVENCAPI_VERSION is major | minor << 24; the driver reports major << 4 | minor. Regenerating against another
        // header without moving RequiredVersion would ask the driver for one API and lay out structs for another.
        Assert.Equal(NvEnc.ApiVersion & 0xFF, NvEncApi.RequiredVersion >> 4);
        Assert.Equal(NvEnc.ApiVersion >> 24, NvEncApi.RequiredVersion & 0xF);
    }

    [Fact]
    public void Struct_versions_carry_the_api_version()
    {
        // NVENCAPI_STRUCT_VERSION(v) is NVENCAPI_VERSION | v << 16 | 0x7 << 28, some with bit 31 set as well.
        foreach (uint version in new[]
                 {
                     NvEnc.OpenEncodeSessionExParamsVer, NvEnc.FunctionListVer, NvEnc.CapsParamVer, NvEnc.ConfigVer,
                     NvEnc.PresetConfigVer, NvEnc.InitializeParamsVer, NvEnc.PicParamsVer, NvEnc.LockBitstreamVer,
                 })
        {
            Assert.Equal(NvEnc.ApiVersion, version & 0x0F00FFFF);
            Assert.Equal(0x70000000u, version & 0x70000000);
        }
    }

    [Fact]
    public void The_preset_config_holds_a_whole_config()
    {
        Assert.True(Unsafe.SizeOf<NV_ENC_PRESET_CONFIG>() > Unsafe.SizeOf<NV_ENC_CONFIG>());
    }

    [Fact]
    public void Versions_read_as_major_dot_minor()
    {
        Assert.Equal("12.1", NvEncApi.FormatVersion(NvEncApi.RequiredVersion));
        Assert.Equal("13.0", NvEncApi.FormatVersion(13 << 4));
    }
}
