using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Orrery.App.Controls;
using Orrery.App.Settings;
using Orrery.Core.Credentials;
using Orrery.Core.Settings;
using Orrery.Core.Storage;
using Vision = Orrery.Core.Settings.ColorVision;
using Xunit;

namespace Orrery.App.Tests;

public class SettingsViewTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));

    [AvaloniaFact]
    public void A_side_list_picks_one_page_at_a_time_and_the_last_one_is_remembered()
    {
        var location = new DataLocation(StorageMode.Portable, _root, Path.Combine(_root, "cache"), Path.Combine(_root, "logs"));
        var settings = AppSettingsStore.In(location);
        using var vault = new VaultFileStore(location.FileIn("vault.frv"), autoLockAfter: CredentialStores.DefaultAutoLock);
        var view = new SettingsView(location, settings, vault, () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask);
        var window = new Window { Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var list = Assert.Single(window.GetVisualDescendants().OfType<ListBox>(), l => l.Classes.Contains("side"));
        Assert.Equal("Settings pages", AutomationProperties.GetName(list));
        Assert.Equal(["General", "Appearance", "Sessions", "Data and security", "About"], list.Items.OfType<ListBoxItem>().Select(i => (string)i.Content!));
        Assert.Equal("General", view.CurrentPage);
        Assert.Null(settings.Load().SettingsPage);

        // Every section sits on exactly one page, and only that page's headings show.
        static List<string> Headings(Window w) =>
            w.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("section") && t.IsEffectivelyVisible).Select(t => t.Text!).ToList();
        Assert.Equal(["START-UP", "LAYOUT", "KEYBOARD", "HOST STATUS"], Headings(window));

        list.SelectedItem = list.Items.OfType<ListBoxItem>().Single(i => Equals(i.Content, "Appearance"));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Appearance", view.CurrentPage);
        Assert.Equal(["COLOR VISION", "INTERFACE COLORS", "TERMINAL COLORS", "ACCESSIBILITY"], Headings(window));
        Assert.Equal("Appearance", settings.Load().SettingsPage);
        window.Close();

        var again = new SettingsView(location, settings, vault, () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask);
        Assert.Equal("Appearance", again.CurrentPage);

        var all = new SettingsView(location, settings, vault, () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask);
        var shown = new List<string>();
        var allWindow = new Window { Content = all };
        allWindow.Show();
        foreach (var page in new[] { "General", "Appearance", "Sessions", "Data and security", "About" })
        {
            all.ShowPage(page);
            Dispatcher.UIThread.RunJobs();
            shown.AddRange(Headings(allWindow));
        }

        Assert.Equal(18, shown.Count);
        Assert.Equal(shown.Count, shown.Distinct().Count());
        allWindow.Close();
    }

    [AvaloniaFact]
    public void A_remembered_page_that_no_longer_exists_opens_the_first()
    {
        var location = new DataLocation(StorageMode.Portable, _root, Path.Combine(_root, "cache"), Path.Combine(_root, "logs"));
        var settings = AppSettingsStore.In(location);
        settings.Save(new AppSettings { SettingsPage = "Gone" });
        using var vault = new VaultFileStore(location.FileIn("vault.frv"), autoLockAfter: CredentialStores.DefaultAutoLock);

        var view = new SettingsView(location, settings, vault, () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask);

        Assert.Equal("General", view.CurrentPage);
    }

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

        window.KeyPressQwerty(PhysicalKey.F12, RawInputModifiers.Control | RawInputModifiers.Alt);
        Assert.False(recorder.IsRecording);
        Assert.Equal(Key.F12, changed?.Key);
        Assert.Equal("Ctrl+Alt+F12", settings.Load().FullScreenShortcut);
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

        var autoLock = Assert.Single(window.GetVisualDescendants().OfType<ComboBox>(),
            c => Avalonia.Automation.AutomationProperties.GetName(c) == "Lock the vault when unused for");
        Assert.Equal(30, (int)((ComboBoxItem)autoLock.SelectedItem!).Tag!);

        autoLock.SelectedItem = autoLock.Items.OfType<ComboBoxItem>().Single(i => (int)i.Tag! == 0);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, settings.Load().AutoLockMinutes);
        Assert.Null(vault.AutoLockAfter);
        window.Close();
    }

    [AvaloniaFact]
    public void Turning_terminal_announcements_on_or_off_saves_the_choice_and_says_so()
    {
        var location = new DataLocation(StorageMode.Portable, _root, Path.Combine(_root, "cache"), Path.Combine(_root, "logs"));
        var settings = AppSettingsStore.In(location);
        using var vault = new VaultFileStore(location.FileIn("vault.frv"), autoLockAfter: CredentialStores.DefaultAutoLock);
        var view = new SettingsView(location, settings, vault, () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask);
        var changes = new List<bool>();
        view.TerminalAnnouncementsChanged += (_, on) => changes.Add(on);
        var window = new Window { Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var announce = window.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Content as string == "Send new terminal output to your screen reader");
        Assert.Null(settings.Load().TerminalAnnouncements);
        bool start = announce.IsChecked == true;

        // Turned on with no screen reader running, a warning says nothing will be heard.
        static bool Warned(Window w) => w.GetVisualDescendants().OfType<Notice>().Any(n => n.IsVisible && n.Message.Text?.StartsWith("No screen reader is running") == true);
        Assert.Equal(OperatingSystem.IsWindows() && start && !ScreenReader.IsRunning, Warned(window));

        announce.IsChecked = !start;
        Assert.Equal(!start, settings.Load().TerminalAnnouncements);
        Assert.Equal(OperatingSystem.IsWindows() && !start && !ScreenReader.IsRunning, Warned(window));
        announce.IsChecked = start;
        Assert.Equal(start, settings.Load().TerminalAnnouncements);
        Assert.Equal([!start, start], changes);
        window.Close();
    }

    [AvaloniaFact]
    public void Color_settings_are_saved_and_applied_at_once()
    {
        var location = new DataLocation(StorageMode.Portable, _root, Path.Combine(_root, "cache"), Path.Combine(_root, "logs"));
        var settings = AppSettingsStore.In(location);
        using var vault = new VaultFileStore(location.FileIn("vault.frv"), autoLockAfter: CredentialStores.DefaultAutoLock);
        var view = new SettingsView(location, settings, vault, () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask);
        TerminalColorScheme? terminalScheme = null;
        view.TerminalColorsChanged += (_, scheme) => terminalScheme = scheme;
        var window = new Window { Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        try
        {
            RadioButton Choice(InterfaceColorScheme scheme) =>
                Assert.Single(window.GetVisualDescendants().OfType<RadioButton>(), r => Equals(r.Tag, scheme));
            Assert.Equal(3, window.GetVisualDescendants().OfType<RadioButton>().Count(r => r.Tag is InterfaceColorScheme));
            Assert.True(Choice(InterfaceColorScheme.Standard).IsChecked);

            Choice(InterfaceColorScheme.Clearer).IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(InterfaceColorScheme.Clearer, settings.Load().InterfaceColors);
            Assert.Equal(InterfaceColorScheme.Clearer, Palette.Scheme);
            Assert.Equal(Palette.Clearer.StateUp, Palette.StateUp.Color);

            Choice(InterfaceColorScheme.ColorBlind).IsChecked = true;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(InterfaceColorScheme.ColorBlind, settings.Load().InterfaceColors);
            Assert.Null(settings.Load().ColorBlindColors);
            Assert.Equal(InterfaceColorScheme.ColorBlind, Palette.Scheme);
            Assert.Equal(Palette.ColorBlind.Danger, Palette.Danger.Color);
            Assert.Equal(Palette.ColorBlind.StateUp, Palette.StateUp.Color);

            var terminal = Assert.Single(window.GetVisualDescendants().OfType<ComboBox>(), c => c.Items.OfType<ComboBoxItem>().Any(i => i.Tag is TerminalColorScheme));
            terminal.SelectedItem = terminal.Items.OfType<ComboBoxItem>().Single(i => Equals(i.Tag, TerminalColorScheme.ColorBlind));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(TerminalColorScheme.ColorBlind, settings.Load().TerminalColors);
            Assert.Equal(TerminalColorScheme.ColorBlind, terminalScheme);

            Choice(InterfaceColorScheme.Standard).IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.Null(settings.Load().InterfaceColors);
            Assert.Equal(Palette.Standard.Danger, Palette.Danger.Color);
        }
        finally
        {
            Palette.Use(InterfaceColorScheme.Standard);
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_color_vision_picks_the_color_blind_sets_and_leaves_them_to_the_user()
    {
        var location = new DataLocation(StorageMode.Portable, _root, Path.Combine(_root, "cache"), Path.Combine(_root, "logs"));
        var settings = AppSettingsStore.In(location);
        using var vault = new VaultFileStore(location.FileIn("vault.frv"), autoLockAfter: CredentialStores.DefaultAutoLock);
        var view = new SettingsView(location, settings, vault, () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask);
        var changes = new List<(Vision, int)>();
        view.ColorVisionChanged += (_, change) => changes.Add(change);
        var window = new Window { Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        try
        {
            var vision = Assert.Single(window.GetVisualDescendants().OfType<ComboBox>(), c => c.Items.OfType<ComboBoxItem>().Any(i => i.Tag is Vision));
            var strength = Assert.Single(window.GetVisualDescendants().OfType<Slider>());
            var terminal = Assert.Single(window.GetVisualDescendants().OfType<ComboBox>(), c => c.Items.OfType<ComboBoxItem>().Any(i => i.Tag is TerminalColorScheme));
            RadioButton Choice(InterfaceColorScheme scheme) =>
                Assert.Single(window.GetVisualDescendants().OfType<RadioButton>(), r => Equals(r.Tag, scheme));

            // Named for a screen reader, with the explanation as help text.
            Assert.Equal("Color vision", AutomationProperties.GetName(vision));
            Assert.Equal("Correction strength", AutomationProperties.GetName(strength));
            Assert.False(string.IsNullOrEmpty(AutomationProperties.GetHelpText(strength)));
            Assert.False(strength.IsEnabled);

            vision.SelectedItem = vision.Items.OfType<ComboBoxItem>().Single(i => Equals(i.Tag, Vision.Deuteranopia));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(Vision.Deuteranopia, settings.Load().ColorVision);
            Assert.Equal(InterfaceColorScheme.ColorBlind, settings.Load().InterfaceColors);
            Assert.Equal(TerminalColorScheme.ColorBlind, settings.Load().TerminalColors);
            Assert.Equal(InterfaceColorScheme.ColorBlind, Palette.Scheme);
            Assert.True(Choice(InterfaceColorScheme.ColorBlind).IsChecked);
            Assert.Equal(TerminalColorScheme.ColorBlind, (terminal.SelectedItem as ComboBoxItem)?.Tag);
            Assert.True(strength.IsEnabled);

            strength.Value = 60;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(60, settings.Load().ColorCorrectionStrength);

            // The palette can still be changed by hand, and the color vision stays.
            Choice(InterfaceColorScheme.Clearer).IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(InterfaceColorScheme.Clearer, settings.Load().InterfaceColors);
            Assert.Equal(Vision.Deuteranopia, settings.Load().ColorVision);

            // Back to none: the palettes stay as they are, and the strength can't be changed.
            vision.SelectedItem = vision.Items.OfType<ComboBoxItem>().Single(i => Equals(i.Tag, Vision.None));
            Dispatcher.UIThread.RunJobs();
            Assert.Null(settings.Load().ColorVision);
            Assert.Equal(InterfaceColorScheme.Clearer, settings.Load().InterfaceColors);
            Assert.False(strength.IsEnabled);

            Assert.Equal([(Vision.Deuteranopia, 100), (Vision.Deuteranopia, 60), (Vision.None, 60)], changes);
        }
        finally
        {
            Palette.Use(InterfaceColorScheme.Standard);
            window.Close();
        }
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
