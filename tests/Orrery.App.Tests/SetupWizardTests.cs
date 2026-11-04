using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Orrery.App.Controls;
using Orrery.App.Setup;
using Orrery.Core.Credentials;
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

        Assert.Equal([SetupStep.Data, SetupStep.Passwords, SetupStep.Import, SetupStep.Updates, SetupStep.Network, SetupStep.Done], window.Steps);
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<RadioButton>(), r => r.GroupName == "Passwords" && r.IsEffectivelyVisible);
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
    public void Without_a_keychain_only_the_vaults_password_is_asked_and_it_can_wait()
    {
        var window = Open(portable: null, keychain: null);

        Assert.Equal([SetupStep.Passwords, SetupStep.Import, SetupStep.Updates, SetupStep.Network, SetupStep.Done], window.Steps);
        Assert.True(VaultAutoLockTests.Private<TextBox>(window, "_vaultPassword").IsEffectivelyVisible);
        Assert.Equal("Master password", VaultAutoLockTests.Private<TextBlock>(window, "_title").Text);
        Assert.Null(window.Choices.Passwords);
        Assert.Contains(("Passwords", "Encrypted vault, master password when first needed"), window.Summary());
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_vaults_password_is_asked_only_once_the_vault_is_chosen_and_must_be_good_and_match()
    {
        var window = Open(portable: null, Keychain);
        var password = VaultAutoLockTests.Private<TextBox>(window, "_vaultPassword");
        var confirm = VaultAutoLockTests.Private<TextBox>(window, "_vaultConfirm");
        var message = VaultAutoLockTests.Private<Notice>(window, "_message");
        Assert.False(password.IsEffectivelyVisible);

        // Typed, then the keychain chosen after all: nothing is asked of it, and no vault is made.
        VaultAutoLockTests.Private<RadioButton>(window, "_vault").IsChecked = true;
        Assert.True(password.IsEffectivelyVisible);
        password.Text = "short";
        VaultAutoLockTests.Private<RadioButton>(window, "_keychain").IsChecked = true;
        Assert.Null(window.VaultPassword);
        await window.NextAsync();
        Assert.Equal(SetupStep.Import, window.Current);

        window.Back();
        VaultAutoLockTests.Private<RadioButton>(window, "_vault").IsChecked = true;
        await window.NextAsync();
        Assert.Equal(SetupStep.Passwords, window.Current);
        Assert.True(message.IsVisible);

        password.Text = "correct horse battery staple";
        confirm.Text = "correct horse battery stapler";
        await window.NextAsync();
        Assert.Equal(SetupStep.Passwords, window.Current);
        Assert.Equal("The passwords don't match.", message.Text);

        confirm.Text = password.Text;
        await window.NextAsync();
        Assert.Equal(SetupStep.Import, window.Current);
        Assert.Contains(("Passwords", "Encrypted vault, master password set"), window.Summary());
        window.Close();
    }

    [AvaloniaFact]
    public async Task Start_hands_over_the_vaults_password_once_and_checks_it_even_when_its_step_was_skipped()
    {
        var window = Open(portable: null, keychain: null);
        SetupResult? result = null;
        window.Completed += r => result = r;
        var password = VaultAutoLockTests.Private<TextBox>(window, "_vaultPassword");
        var confirm = VaultAutoLockTests.Private<TextBox>(window, "_vaultConfirm");

        password.Text = "correct horse battery staple";
        window.GoTo(SetupStep.Done);
        await window.NextAsync();
        Assert.Null(result);
        Assert.Equal(SetupStep.Passwords, window.Current);

        confirm.Text = password.Text;
        window.GoTo(SetupStep.Done);
        await window.NextAsync();
        window.Close();

        Assert.NotNull(result);
        Assert.Equal("correct horse battery staple", result.VaultPassword);
        Assert.DoesNotContain("correct horse", result.ToString());
        Assert.Empty(password.Text ?? string.Empty);
        Assert.Empty(confirm.Text ?? string.Empty);
    }

    [AvaloniaFact]
    public async Task The_main_window_makes_the_vault_with_setups_password_before_anything_asks_for_it()
    {
        var location = new DataLocation(StorageMode.Portable, Path.Combine(_dir, "data"), Path.Combine(_dir, "cache"), Path.Combine(_dir, "logs"));
        Directory.CreateDirectory(location.DataDir);
        VaultAutoLockTests.AddMainWindowResources();
        var window = new MainWindow(location, newVaultPassword: "correct horse battery staple");
        try
        {
            var vault = Assert.IsType<VaultFileStore>(VaultAutoLockTests.Private<ICredentialStore>(window, "_credentials"));
            Assert.False(vault.Exists);

            window.Show();
            for (int i = 0; i < 500 && vault.IsLocked; i++)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }

            Assert.True(vault.Exists);
            Assert.False(vault.IsLocked);

            vault.Lock();
            await vault.UnlockAsync("correct horse battery staple");
            Assert.False(vault.IsLocked);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Skip_to_the_end_keeps_a_typed_vault_password()
    {
        var window = Open(portable: null, keychain: null);
        var password = VaultAutoLockTests.Private<TextBox>(window, "_vaultPassword");
        password.Text = VaultAutoLockTests.Private<TextBox>(window, "_vaultConfirm").Text = "correct horse battery staple";

        window.SkipToEnd();

        Assert.Equal("correct horse battery staple", window.VaultPassword);
        Assert.Contains(("Passwords", "Encrypted vault, master password set"), window.Summary());
        Assert.False(VaultAutoLockTests.Private<Notice>(window, "_message").IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Skip_to_the_end_says_when_the_master_password_cant_be_used()
    {
        var window = Open(portable: null, keychain: null);
        VaultAutoLockTests.Private<TextBox>(window, "_vaultPassword").Text = "correct horse battery staple";
        VaultAutoLockTests.Private<TextBox>(window, "_vaultConfirm").Text = "something else entirely";

        window.SkipToEnd();

        var message = VaultAutoLockTests.Private<Notice>(window, "_message");
        Assert.Equal(SetupStep.Done, window.Current);
        Assert.True(message.IsVisible);
        Assert.Contains("don't match", message.Text);

        // Start takes it back to the step to fix it.
        await window.NextAsync();
        Assert.Equal(SetupStep.Passwords, window.Current);
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
    public void Skip_to_the_end_lists_the_defaults_when_nothing_was_changed()
    {
        var window = Open(portable: null, Keychain);

        Named(window, "SetupDefaults").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(SetupStep.Done, window.Current);
        Assert.Equal(
        [
            ("Passwords", Keychain),
            ("Import", "mRemoteNG, SSH config"),
            ("Updates", "Checked daily; ask me"),
            ("Previews", new AppSettings().OffersPreviewUpdates ? "On" : "Off"),
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
    public void Skip_to_the_end_keeps_what_was_already_chosen()
    {
        var window = Open(portable: null, Keychain);
        VaultAutoLockTests.Private<RadioButton>(window, "_vault").IsChecked = true;
        VaultAutoLockTests.Private<ToggleSwitch>(window, "_previews").IsChecked = true;
        VaultAutoLockTests.Private<RadioButton>(window, "_download").IsChecked = true;
        window.GetVisualDescendants().OfType<ToggleSwitch>().Single(t => Avalonia.Automation.AutomationProperties.GetName(t) == "Import mRemoteNG").IsChecked = false;

        window.SkipToEnd();

        var choices = window.Choices;
        Assert.Equal(CredentialStoreKind.VaultFile, choices.Passwords);
        Assert.True(choices.UpdatePreviews);
        Assert.Equal(UpdateAction.Download, choices.WhenUpdateFound);
        Assert.Equal([Found()[1].Path], choices.Imports);
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
        Assert.True(saved.OffersPreviewUpdates);
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

        window.SkipToEnd();
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

    [AvaloniaFact]
    public async Task A_password_protected_file_says_its_password_comes_after_setup()
    {
        Directory.CreateDirectory(_dir);
        var sealedFile = Path.Combine(_dir, "sealed.json");
        var plainFile = Path.Combine(_dir, "plain.json");
        File.WriteAllText(sealedFile, $$"""{"format":"{{Orrery.Core.Export.SealedExport.FormatName}}"}""");
        File.WriteAllText(plainFile, "{}");
        var window = Open(portable: null, keychain: null, found: []);
        window.GoTo(SetupStep.Import);

        window.AddFile(sealedFile);
        window.AddFile(plainFile);
        await window.PasswordChecks;

        Assert.Contains(("Import", "Orrery export (needs its password), Orrery export"), window.Summary());
        Assert.Single(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == SetupWizard.ProtectedNote);
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
