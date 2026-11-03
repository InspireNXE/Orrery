using System.Reflection;
using Orrery.Core.Updates;
using Xunit;

namespace Orrery.Core.Tests.Updates;

public class ReleaseVersionTests
{
    [Theory]
    [InlineData("0.1.0", 0, 1, 0, null)]
    [InlineData("v0.2.0", 0, 2, 0, null)]
    [InlineData("V10.20.30", 10, 20, 30, null)]
    [InlineData("0.2.0-preview.1", 0, 2, 0, "preview.1")]
    [InlineData("1.0.0-rc.1+abc123", 1, 0, 0, "rc.1")]
    [InlineData("0.1.0+7ecb185", 0, 1, 0, null)]
    public void Parses_versions_and_tags(string text, int major, int minor, int patch, string? prerelease)
    {
        Assert.Equal(new ReleaseVersion(major, minor, patch, prerelease), ReleaseVersion.Parse(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("01.2.3")]
    [InlineData("1.2.x")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-a..b")]
    [InlineData("nightly")]
    public void Refuses_what_isnt_a_version(string? text)
    {
        Assert.False(ReleaseVersion.TryParse(text, out _));
    }

    [Fact]
    public void Orders_the_way_semantic_versioning_does()
    {
        string[] ascending =
        [
            "0.1.0", "0.1.1", "0.2.0-alpha", "0.2.0-alpha.1", "0.2.0-alpha.beta", "0.2.0-beta", "0.2.0-beta.2",
            "0.2.0-beta.11", "0.2.0-rc.1", "0.2.0", "0.10.0", "1.0.0",
        ];

        for (int i = 1; i < ascending.Length; i++)
        {
            var lower = ReleaseVersion.Parse(ascending[i - 1]);
            var higher = ReleaseVersion.Parse(ascending[i]);
            Assert.True(lower < higher, $"{lower} < {higher}");
            Assert.True(higher > lower, $"{higher} > {lower}");
        }
    }

    [Fact]
    public void A_tag_and_its_version_are_the_same_version()
    {
        Assert.Equal(ReleaseVersion.Parse("v0.2.0-preview.1"), ReleaseVersion.Parse("0.2.0-preview.1"));
        Assert.Equal(0, ReleaseVersion.Parse("v1.0.0").CompareTo(ReleaseVersion.Parse("1.0.0+build")));
        Assert.Equal("0.2.0-preview.1", ReleaseVersion.Parse("v0.2.0-preview.1+abc").ToString());
    }

    [Fact]
    public void Reads_an_assemblys_informational_version_without_the_commit()
    {
        var version = ReleaseVersion.OfAssembly(typeof(ReleaseVersion).Assembly);
        var informational = typeof(ReleaseVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

        Assert.Equal(informational.Split('+')[0], version.ToString());
    }
}
