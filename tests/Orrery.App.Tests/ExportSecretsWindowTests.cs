using Orrery.App.Connections;
using Xunit;

namespace Orrery.App.Tests;

public class ExportSecretsWindowTests
{
    [Fact]
    public void Leaving_secrets_out_needs_no_password()
    {
        Assert.Null(ExportSecretsWindow.Problem(include: false, "", ""));
    }

    [Fact]
    public void Including_them_needs_a_long_enough_password_typed_twice_the_same()
    {
        Assert.Contains("at least", ExportSecretsWindow.Problem(include: true, "short", "short"));
        Assert.Contains("don't match", ExportSecretsWindow.Problem(include: true, "long enough one", "long enough two"));
        Assert.Null(ExportSecretsWindow.Problem(include: true, "long enough", "long enough"));
    }
}
