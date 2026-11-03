using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Orrery.App.Setup;
using Orrery.Core.Settings;
using Orrery.Core.Storage;
using Xunit;

namespace Orrery.App.Tests;

/// <summary>First-launch setup: which steps show, moving between them, and what Start saves.</summary>
public class SetupWizardTests : IDisposable
{
    const string Keychain = "Windows Credential Manager";
    readonly string _dir = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    DataLocation Installed(string name = "home")
    {
        var config = Path.Combine(_dir, name, "config");
        return new DataLocation(StorageMode.Installed, config, Path.Combine(_dir, name, "cache"), Path.Combine(_dir, name, "logs"));
    }

    PortableSetup Portable()
    {
        var app = Path.Combine(_dir, "app");
        Directory.CreateDirectory(app);
        return new PortableSetup(app, new PlatformFolders(Path.Combine(_dir, "profile"), Path.Combine(_dir, "cache"), Path.Combine(_dir, "logs")));
    }

    static IReadOnlyList<FoundImport> Found() =>
    [
        new(Path.Combine("C:", "Users", "me", "AppData", "Roaming", "mRemoteNG", "confCons.xml"), "mRemoteNG"),
        new(Path.Combine("C:", "Users", "me", ".ssh", "config"), "SSH config"),
    ];

    static SetupWizard Open(PortableSetup? portable, string? keychain, IReadOnlyList<FoundImport>? found = null)
    {
        var window = new SetupWizard(portable, keychain, found ?? Found());
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    static Button Named(Window window, string name) => window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == name);

    [AvaloniaFact]
    public void A_portable_copy_asks_where_its_data_goes_and_never_about_the_keychain()
    {
        var window = Open(Portable(), keychain: null);

        Assert.Equal([SetupStep.Data, SetupStep.Import, SetupStep.Updates, SetupStep.Network, SetupStep.Done], window.Steps);
        Assert.Equal(SetupStep.Data, window.Current);
        window.Close();
    }

    [AvaloniaFact]
    public void An_installed_copy_with_a_keychain_starts_with_passwords()
    {
        var window = Open(portable: null, Keychain);

        Assert.Equal([SetupStep.Passwords, SetupStep.Import, SetupStep.Updates, SetupStep.Network, SetupStep.Done], window.Steps);
        var steps = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "SetupSteps");
        Assert.Equal(["Passwords", "Import", "Updates", "Network", "Done"],
            steps.Items.OfType<ListBoxItem>().Select(i => Avalonia.Automation.AutomationProperties.GetName(i)));
        window.Close();
    }

    [AvaloniaFact]
    public void Without_a_keychain_there_is_no_password_question()
    {
        var window = Open(portable: null, keychain: null);

        Assert.Equal([SetupStep.Import, SetupStep.Updates, SetupStep.Network, SetupStep.Done], window.Steps);
        Assert.Null(window.Choices.Passwords);
        Assert.Contains(("Passwords", "Encrypted vault"), window.Summary());
        window.Close();
    }

    [AvaloniaFact]
    public async Task Back_is_off_on_the_first_step_and_next_says_start_on_the_last()
    {
        var window = Open(portable: null, Keychain);
        var back = Named(window, "SetupBack");
        var next = Named(window, "SetupNext");

        Assert.False(back.IsEnabled);
        Assert.Equal("1 of 5", VaultAutoLockTests.Private<TextBlock>(window, "_progress").Text);

        await window.NextAsync();
        Assert.Equal(SetupStep.Import, window.Current);
        Assert.True(back.IsEnabled);
        Assert.Equal("Next", next.Content);

        window.Back();
        Assert.Equal(SetupStep.Passwords, window.Current);
        Assert.False(back.IsEnabled);

        window.GoTo(SetupStep.Done);
        Assert.Equal("Start", next.Content);
        Assert.False(Named(window, "SetupDefaults").IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void Use_defaults_jumps_to_done_and_lists_them()
    {
        var window = Open(portable: null, Keychain);

        // Changed, then put back by Use defaults.
        VaultAutoLockTests.Private<RadioButton>(window, "_vault").IsChecked = true;
        VaultAutoLockTests.Private<ToggleSwitch>(window, "_previews").IsChecked = true;
        Named(window, "SetupDefaults").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(SetupStep.Done, window.Current);
        Assert.Equal(
        [
            ("Passwords", Keychain),
            ("Import", "mRemoteNG, SSH config"),
            ("Updates", "Checked daily; ask me"),
            ("Previews", "Off"),
            ("Host status", "Shown"),
            ("Web pages", "In a tab"),
        ], window.Summary());

        var choices = window.Choices;
        Assert.Equal(CredentialStoreKind.OsKeychain, choices.Passwords);
        Assert.Equal(Found().Select(f => f.Path), choices.Imports);
        Assert.Equal(new SetupChoices { Passwords = CredentialStoreKind.OsKeychain, Imports = choices.Imports }.ApplyTo(new AppSettings()),
            choices.ApplyTo(new AppSettings()));

        // The summary is on screen, each row named for a screen reader.
        Assert.Contains(window.GetVisualDescendants().OfType<SelectableTextBlock>(),
            t => t.IsEffectivelyVisible && Avalonia.Automation.AutomationProperties.GetName(t) == "Import: mRemoteNG, SSH config");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Start_hands_over_the_choices_and_they_are_saved_with_setup_done()
    {
        var window = Open(portable: null, Keychain);
        SetupResult? result = null;
        window.Completed += r => result = r;

        VaultAutoLockTests.Private<RadioButton>(window, "_vault").IsChecked = true;
        VaultAutoLockTests.Private<RadioButton>(window, "_download").IsChecked = true;
        VaultAutoLockTests.Private<ToggleSwitch>(window, "_previews").IsChecked = true;
        VaultAutoLockTests.Private<ToggleSwitch>(window, "_hostChecks").IsChecked = false;
        VaultAutoLockTests.Private<RadioButton>(window, "_inBrowser").IsChecked = true;
        var imports = VaultAutoLockTests.Private<System.Collections.IList>(window, "_imports");
        Assert.Equal(2, imports.Count);
        window.GetVisualDescendants().OfType<ToggleSwitch>().Single(t => Avalonia.Automation.AutomationProperties.GetName(t) == "Import mRemoteNG").IsChecked = false;

        window.GoTo(SetupStep.Done);
        await window.NextAsync();
        window.Close();

        Assert.NotNull(result);
        Assert.Null(result.Location);
        Assert.Equal([Found()[1].Path], result.Choices.Imports);

        var location = Installed();
        App.SaveSetup(location, result.Choices);
        var saved = AppSettingsStore.In(location).Load();
        Assert.True(saved.SetupDone);
        Assert.Equal(CredentialStoreKind.VaultFile, saved.CredentialStore);
        Assert.Equal(UpdateAction.Download, saved.WhenUpdateFound);
        Assert.True(saved.UpdatePreviews);
        Assert.False(saved.HostStatusChecks);
        Assert.False(saved.WebPagesInOrrery);
        Assert.Null(saved.UpdateChecks);
    }

    [AvaloniaFact]
    public void Defaults_are_saved_as_unset_apart_from_setup_done()
    {
        var location = Installed();
        App.SaveSetup(location, new SetupChoices { Passwords = CredentialStoreKind.OsKeychain });

        Assert.Equal(new AppSettings { SetupDone = true }, AppSettingsStore.In(location).Load());
    }

    [AvaloniaFact]
    public async Task A_portable_start_with_the_defaults_keeps_the_data_beside_orrery()
    {
        var portable = Portable();
        if (FolderAccess.IsOpenToOthers(portable.AppDirectory))
        {
            return; // The open-folder question would wait for an answer.
        }

        var window = Open(portable, keychain: null, found: []);
        SetupResult? result = null;
        window.Completed += r => result = r;

        window.UseDefaults();
        await window.NextAsync();
        window.Close();

        Assert.NotNull(result);
        Assert.Equal(Path.Combine(portable.AppDirectory, "data"), result.Location!.DataDir);
        Assert.Equal(StorageMode.Portable, result.Location.Mode);
        Assert.Equal(PortableDataChoice.WithApp, PortableMarker.TryLoad(portable.AppDirectory)!.Choice);
    }

    [AvaloniaFact]
    public async Task Somewhere_else_with_no_folder_stays_on_the_data_step_and_says_why()
    {
        var window = Open(Portable(), keychain: null);
        VaultAutoLockTests.Private<RadioButton>(window, "_elsewhere").IsChecked = true;

        await window.NextAsync();

        Assert.Equal(SetupStep.Data, window.Current);
        var message = VaultAutoLockTests.Private<Orrery.App.Controls.Notice>(window, "_message");
        Assert.True(message.IsVisible);
        Assert.Equal("Choose a folder first.", message.Text);
        window.Close();
    }

    [AvaloniaFact]
    public void Enter_moves_on_from_an_option_and_escape_changes_nothing()
    {
        var window = Open(portable: null, Keychain);
        window.GoTo(SetupStep.Updates);
        var download = VaultAutoLockTests.Private<RadioButton>(window, "_download");
        download.IsChecked = true;
        download.Focus();

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.IsVisible);
        Assert.Equal(SetupStep.Updates, window.Current);
        Assert.Equal(UpdateAction.Download, window.Choices.WhenUpdateFound);

        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(SetupStep.Network, window.Current);
        Assert.Equal(UpdateAction.Download, window.Choices.WhenUpdateFound);
        window.Close();
    }

    [AvaloniaFact]
    public void Every_step_names_its_controls_and_tab_reaches_the_footer()
    {
        var window = Open(Portable(), Keychain);
        foreach (var step in window.Steps)
        {
            window.GoTo(step);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            AccessibilityAudit.AssertAllNamed(window);
        }

        window.GoTo(SetupStep.Updates);
        Dispatcher.UIThread.RunJobs();
        var order = AccessibilityAudit.TabOrder(VaultAutoLockTests.Private<ToggleSwitch>(window, "_updateChecks"));
        int previews = order.IndexOf(VaultAutoLockTests.Private<ToggleSwitch>(window, "_previews"));
        int next = order.IndexOf(Named(window, "SetupNext"));
        Assert.True(previews >= 0 && next > previews, "Tab goes through the options, then to Next.");
        window.Close();
    }

    [AvaloniaFact]
    public void With_nothing_found_the_import_step_says_so_and_another_file_adds_a_row()
    {
        var window = Open(portable: null, keychain: null, found: []);
        window.GoTo(SetupStep.Import);
        Assert.Equal("Nothing found on this computer.", VaultAutoLockTests.Private<TextBlock>(window, "_subtitle").Text);
        Assert.Empty(window.Choices.Imports);

        var file = Path.Combine(_dir, "export.json");
        window.AddFile(file);
        window.AddFile(file);

        Assert.Equal([file], window.Choices.Imports);
        Assert.Contains(("Import", "Orrery export"), window.Summary());
        window.Close();
    }

    // ---- When setup runs ----

    [Fact]
    public void A_portable_copy_still_to_choose_gets_setup_with_the_data_step()
    {
        Assert.Equal(SetupKind.WithDataStep, FirstLaunch.Decide(new StartupStorage.NeedsPortableChoice(_dir)));
    }

    [Fact]
    public void An_empty_data_folder_gets_setup_without_the_data_step()
    {
        var location = Installed();
        Assert.Equal(SetupKind.WithoutDataStep, FirstLaunch.Decide(new StartupStorage.Ready(location)));

        Directory.CreateDirectory(location.DataDir);
        Assert.Equal(SetupKind.WithoutDataStep, FirstLaunch.Decide(new StartupStorage.Ready(location)));
    }

    [Fact]
    public void A_folder_with_settings_from_before_setup_existed_gets_no_setup()
    {
        var location = Installed();
        AppSettingsStore.In(location).Save(new AppSettings { TerminalFontSize = 16 });

        Assert.Null(AppSettingsStore.In(location).Load().SetupDone);
        Assert.Equal(SetupKind.None, FirstLaunch.Decide(new StartupStorage.Ready(location)));
    }

    [Fact]
    public void A_folder_with_connections_but_no_settings_gets_no_setup()
    {
        var location = Installed();
        Directory.CreateDirectory(location.DataDir);
        File.WriteAllText(location.FileIn("connections.json"), "{}");

        Assert.Equal(SetupKind.None, FirstLaunch.Decide(new StartupStorage.Ready(location)));
    }

    // ---- Files offered for import ----

    [Fact]
    public void Finds_mremoteng_and_the_ssh_config_in_their_usual_places()
    {
        var home = Path.Combine(_dir, "me");
        var appData = Path.Combine(home, "AppData", "Roaming");
        Assert.Empty(ImportSources.Find(home, appData));

        Directory.CreateDirectory(Path.Combine(home, ".ssh"));
        File.WriteAllText(Path.Combine(home, ".ssh", "config"), "Host web\n  HostName web.example\n");
        Directory.CreateDirectory(Path.Combine(appData, "mRemoteNG"));
        File.WriteAllText(Path.Combine(appData, "mRemoteNG", "confCons.xml"), "<Connections />");

        Assert.Equal(
        [
            new FoundImport(Path.Combine(appData, "mRemoteNG", "confCons.xml"), "mRemoteNG"),
            new FoundImport(Path.Combine(home, ".ssh", "config"), "SSH config"),
        ], ImportSources.Find(home, appData));

        // mRemoteNG is looked for only on Windows, which passes its AppData.
        Assert.Equal([new FoundImport(Path.Combine(home, ".ssh", "config"), "SSH config")], ImportSources.Find(home, appData: null));
    }

    [Theory]
    [InlineData("confCons.xml", "mRemoteNG")]
    [InlineData("orrery-export.json", "Orrery export")]
    [InlineData("desk.rdp", "Remote Desktop file")]
    [InlineData("config", "SSH config")]
    public void A_chosen_file_is_named_for_its_kind(string name, string kind) =>
        Assert.Equal(kind, ImportSources.KindOf(Path.Combine(_dir, name)));
}
