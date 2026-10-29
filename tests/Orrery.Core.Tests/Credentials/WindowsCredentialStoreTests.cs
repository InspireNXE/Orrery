using Orrery.Core.Credentials;
using Xunit;

namespace Orrery.Core.Tests.Credentials;

/// <summary>Uses the real Windows Credential Manager and removes what it writes. A no-op elsewhere.</summary>
public class WindowsCredentialStoreTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Secret_round_trips_through_credential_manager_and_index_has_no_secret()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var indexPath = Path.Combine(_dir, "credentials.json");
        var store = new WindowsCredentialStore(indexPath);
        var credential = new Credential(Guid.NewGuid(), "test entry", CredentialKind.Password, "admin", "corp", "Sup3r-Secret!");

        try
        {
            await store.SaveAsync(credential);

            Assert.Equal(credential, await new WindowsCredentialStore(indexPath).GetAsync(credential.Id));
            Assert.DoesNotContain("Sup3r-Secret!", await File.ReadAllTextAsync(indexPath));
        }
        finally
        {
            await store.DeleteAsync(credential.Id);
        }

        Assert.Null(await store.GetAsync(credential.Id));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
