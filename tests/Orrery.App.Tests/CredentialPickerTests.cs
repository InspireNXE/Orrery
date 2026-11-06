using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Orrery.App.Credentials;
using Orrery.Core.Credentials;
using Xunit;

namespace Orrery.App.Tests;

public class CredentialPickerTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));

    // Chosen while the vault was locked, the unlock item rebuilt the list with nothing chosen, and the connection's
    // credential was dropped at the next save of any other change, unlocked or not.
    [AvaloniaFact]
    public async Task Choosing_to_unlock_keeps_the_credential_the_connection_has()
    {
        Directory.CreateDirectory(_dir);
        using var vault = new VaultFileStore(Path.Combine(_dir, "vault.frv"), new VaultFileStore.KdfSettings(MemoryKiB: 8 * 1024, Iterations: 1));
        await vault.UnlockAsync("correct horse battery staple");
        var credential = new Credential(Guid.NewGuid(), "payroll-admin", CredentialKind.Password, "admin", "corp", "Sup3r-Secret!");
        await vault.SaveAsync(credential);
        vault.Lock();

        var picker = new CredentialPicker(vault);
        await picker.RefreshAsync(credential.Id);
        int changes = 0;
        picker.ChoiceChanged += (_, _) => changes++;
        Assert.Equal(credential.Id, picker.SelectedId);

        // Not in a window, so no unlock dialog: as when it's closed without unlocking.
        picker.SelectedItem = picker.Items.OfType<ComboBoxItem>().Single(i => i.Tag is "unlock");

        Assert.Equal(credential.Id, picker.SelectedId);
        Assert.Equal(0, changes);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
