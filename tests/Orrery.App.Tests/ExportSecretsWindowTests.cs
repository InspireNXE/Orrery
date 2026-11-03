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
    public void Including_them_needs_a_password_the_vault_would_take_typed_twice_the_same()
    {
        Assert.Contains("at least 12", ExportSecretsWindow.Problem(include: true, "short", "short"));
        Assert.Contains("common password", ExportSecretsWindow.Problem(include: true, "Password2025!", "Password2025!"));
        Assert.Contains("your name", ExportSecretsWindow.Problem(include: true, "Morgan.Parker!", "Morgan.Parker!", ["morgan", "parker"]));
        Assert.Contains("don't match", ExportSecretsWindow.Problem(include: true, "granite lantern oboe", "granite lantern obo"));
        Assert.Null(ExportSecretsWindow.Problem(include: true, "granite lantern oboe", "granite lantern oboe"));
    }
}
