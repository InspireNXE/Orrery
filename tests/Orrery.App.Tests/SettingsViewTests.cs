using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Orrery.App.Settings;
using Orrery.Core.Credentials;
using Orrery.Core.Settings;
using Orrery.Core.Storage;
using Xunit;

namespace Orrery.App.Tests;

public class SettingsViewTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));

    [AvaloniaFact]
    public void Shows_the_data_folder_and_saves_a_new_auto_lock_time()
    {
        var location = new DataLocation(StorageMode.Portable, _root, Path.Combine(_root, "cache"), Path.Combine(_root, "logs"));
        var settings = AppSettingsStore.In(location);
        using var vault = new VaultFileStore(location.FileIn("vault.frv"), autoLockAfter: CredentialStores.DefaultAutoLock);

        var view = new SettingsView(location, settings, vault, () => Task.CompletedTask, () => Task.CompletedTask);
        var window = new Window { Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains(_root, texts);
        Assert.Contains("Portable", texts);
        Assert.Contains(texts, t => t?.Contains("InspireNXE") == true);

        // Portable copies have no store choice, so the only combo box is the auto-lock time.
        var autoLock = Assert.Single(window.GetVisualDescendants().OfType<ComboBox>());
        Assert.Equal(30, (int)((ComboBoxItem)autoLock.SelectedItem!).Tag!);

        autoLock.SelectedItem = autoLock.Items.OfType<ComboBoxItem>().Single(i => (int)i.Tag! == 0);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, settings.Load().AutoLockMinutes);
        Assert.Null(vault.AutoLockAfter);
        window.Close();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
