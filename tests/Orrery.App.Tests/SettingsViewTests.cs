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
    public void The_import_ssh_config_button_runs_what_it_was_given_and_is_absent_without_one()
    {
        var location = new DataLocation(StorageMode.Portable, _root, Path.Combine(_root, "cache"), Path.Combine(_root, "logs"));
        var settings = AppSettingsStore.In(location);
        using var vault = new VaultFileStore(location.FileIn("vault.frv"), autoLockAfter: CredentialStores.DefaultAutoLock);

        int imported = 0;
        var window = new Window
        {
            Content = new SettingsView(location, settings, vault, () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask,
                () =>
                {
                    imported++;
                    return Task.CompletedTask;
                }),
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var button = Assert.Single(window.GetVisualDescendants().OfType<Button>(), b => b.Content as string == "Import SSH config");
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(1, imported);
        window.Close();

        var without = new Window { Content = new SettingsView(location, settings, vault, () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask) };
        without.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(without.GetVisualDescendants().OfType<Button>(), b => b.Content as string == "Import SSH config");
        without.Close();
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
