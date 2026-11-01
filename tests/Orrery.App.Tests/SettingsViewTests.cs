using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
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
    public void Records_a_full_screen_shortcut_and_refuses_one_that_types_a_character()
    {
        var location = new DataLocation(StorageMode.Portable, _root, Path.Combine(_root, "cache"), Path.Combine(_root, "logs"));
        var settings = AppSettingsStore.In(location);
        using var vault = new VaultFileStore(location.FileIn("vault.frv"), autoLockAfter: CredentialStores.DefaultAutoLock);
        var view = new SettingsView(location, settings, vault, () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask);
        KeyGesture? changed = null;
        view.FullScreenShortcutChanged += (_, shortcut) => changed = shortcut;
        var window = new Window { Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var recorder = Assert.Single(window.GetVisualDescendants().OfType<ShortcutRecorder>());
        recorder.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.True(recorder.IsRecording);

        window.KeyPressQwerty(PhysicalKey.Q, RawInputModifiers.Control | RawInputModifiers.Alt);
        Assert.True(recorder.IsRecording);
        Assert.Null(changed);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.IsVisible && t.Text?.StartsWith("Ctrl+Alt+Q can't be used") == true);

        window.KeyPressQwerty(PhysicalKey.Home, RawInputModifiers.Control | RawInputModifiers.Alt);
        Assert.False(recorder.IsRecording);
        Assert.Equal(Key.Home, changed?.Key);
        Assert.Equal("Ctrl+Alt+Home", settings.Load().FullScreenShortcut);
        window.Close();
    }

    [AvaloniaFact]
    public void Shows_the_data_folder_and_saves_a_new_auto_lock_time()
    {
        var location = new DataLocation(StorageMode.Portable, _root, Path.Combine(_root, "cache"), Path.Combine(_root, "logs"));
        var settings = AppSettingsStore.In(location);
        using var vault = new VaultFileStore(location.FileIn("vault.frv"), autoLockAfter: CredentialStores.DefaultAutoLock);

        var view = new SettingsView(location, settings, vault, () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask);
        var window = new Window { Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains(_root, texts);
        Assert.Contains("Portable", texts);
        Assert.Contains(texts, t => t?.Contains("InspireNXE") == true);

        // Portable copies have no store choice, so the only combo box of times is the auto-lock one (the shell's has names).
        var autoLock = Assert.Single(window.GetVisualDescendants().OfType<ComboBox>(), c => c.Items.OfType<ComboBoxItem>().All(i => i.Tag is int));
        Assert.Equal(30, (int)((ComboBoxItem)autoLock.SelectedItem!).Tag!);

        autoLock.SelectedItem = autoLock.Items.OfType<ComboBoxItem>().Single(i => (int)i.Tag! == 0);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, settings.Load().AutoLockMinutes);
        Assert.Null(vault.AutoLockAfter);
        window.Close();
    }

    [AvaloniaFact]
    public void Saves_the_shell_for_local_terminals()
    {
        var location = new DataLocation(StorageMode.Portable, _root, Path.Combine(_root, "cache"), Path.Combine(_root, "logs"));
        var settings = AppSettingsStore.In(location);
        using var vault = new VaultFileStore(location.FileIn("vault.frv"), autoLockAfter: CredentialStores.DefaultAutoLock);
        var view = new SettingsView(location, settings, vault, () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask);
        var window = new Window { Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var shells = Orrery.Protocols.Local.LocalShells.Available();
        if (shells.Count == 1)
        {
            // Linux and macOS: the login shell, shown but not chosen here.
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == shells[0].Program);
        }
        else
        {
            var choice = Assert.Single(window.GetVisualDescendants().OfType<ComboBox>(), c => c.Items.OfType<ComboBoxItem>().Any(i => i.Tag is "cmd"));
            Assert.Equal(shells[0].Id, ((ComboBoxItem)choice.SelectedItem!).Tag);

            choice.SelectedItem = choice.Items.OfType<ComboBoxItem>().Single(i => i.Tag is "cmd");
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("cmd", settings.Load().LocalShell);
        }

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
