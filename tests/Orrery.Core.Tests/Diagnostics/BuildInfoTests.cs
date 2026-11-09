using Orrery.Core.Diagnostics;
using Xunit;

namespace Orrery.Core.Tests.Diagnostics;

public class BuildInfoTests
{
    [Theory]
    [InlineData("0.2.0-preview.2+5e760e304464f7784eb9478d72e19491fd4ffd58", "0.2.0-preview.2", "5e760e3")]
    [InlineData("0.2.0+5E760E304464F778", "0.2.0", "5e760e3")]
    [InlineData("0.2.0-dev+5e760e3.dirty", "0.2.0-dev", "5e760e3")]
    [InlineData("0.2.0", "0.2.0", null)]
    [InlineData("0.2.0+local", "0.2.0", null)]
    [InlineData("0.2.0+abc12", "0.2.0", null)]
    public void Splits_the_stamped_version_into_the_release_and_a_short_commit(string informational, string version, string? commit)
    {
        Assert.Equal((version, commit), BuildInfo.Parse(informational));
    }

    [Fact]
    public void Shows_the_commit_in_brackets_after_the_version()
    {
        Assert.Equal("0.2.0-preview.2 (5e760e3)", BuildInfo.Format("0.2.0-preview.2", "5e760e3"));
        Assert.Equal("0.2.0", BuildInfo.Format("0.2.0", null));
    }

    [Fact]
    public void This_build_carries_the_commit_it_was_built_from()
    {
        // Built from a git checkout, the SDK stamps the commit; About and the log show it.
        Assert.NotNull(BuildInfo.Commit);
        Assert.Matches("^[0-9a-f]{7}$", BuildInfo.Commit!);
        Assert.Equal($"{BuildInfo.Version} ({BuildInfo.Commit})", BuildInfo.Display);
    }
}
