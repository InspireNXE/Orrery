using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Orrery.App.Credentials;
using Orrery.App.Settings;
using Orrery.Core.Credentials;
using Orrery.Core.Settings;
using Orrery.Core.Storage;
using Xunit;

namespace Orrery.App.Tests;

/// <summary>The unlock window and the Settings switch for Windows Hello or Touch ID, with a stand-in authenticator.</summary>
public class BiometricUnlockUiTests : IDisposable
{
    const string Password = "correct horse battery staple";

    readonly string _root = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));
    readonly TestAuthenticator _hello = new();
    readonly MemorySecrets _records = new();
    readonly TestRemoteSession _remote = new();

    BiometricUnlock Biometric => new(_hello, _records, _remote);

    DataLocation Location => new(StorageMode.Portable, _root, Path.Combine(_root, "cache"), Path.Combine(_root, "logs"));

    async Task<VaultFileStore> NewVaultAsync()
    {
        Directory.CreateDirectory(_root);
        var vault = new VaultFileStore(Location.FileIn("vault.frv"), new VaultFileStore.KdfSettings(8 * 1024, 1));
        await vault.UnlockAsync(Password);
        vault.Lock();
        return vault;
    }

    [AvaloniaFact]
    public async Task The_unlock_window_asks_windows_hello_as_it_opens()
    {
        using var vault = await NewVaultAsync();
        await Biometric.TurnOnAsync(vault, Password);
        var window = new VaultUnlockWindow(vault, Biometric);
        bool? closedWith = null;
        window.Closed += (_, _) => closedWith = !vault.IsLocked;

        window.Show();
        await Settle(() => closedWith is not null);

        Assert.True(closedWith);
        Assert.Equal(2, _hello.Prompts); // One to turn it on, one to unlock.
    }

    [AvaloniaFact]
    public async Task Canceling_the_prompt_leaves_the_password_field_and_the_button()
    {
        using var vault = await NewVaultAsync();
        await Biometric.TurnOnAsync(vault, Password);
        _hello.NextAnswer = AuthenticatorStatus.Canceled;
        var window = new VaultUnlockWindow(vault, Biometric);

        window.Show();
        await Settle(() => _hello.Prompts == 2);

        Assert.True(window.IsVisible);
        Assert.True(vault.IsLocked);
        var button = Buttons(window).Single(b => b.Content as string == "Use Test Hello");
        Assert.True(button.IsVisible);

        // The button tries again.
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Settle(() => !vault.IsLocked);
        window.Close();
    }

    [AvaloniaFact]
    public async Task After_a_password_change_the_unlock_window_says_it_was_turned_off()
    {
        using var vault = await NewVaultAsync();
        await Biometric.TurnOnAsync(vault, Password);
        await vault.ChangePasswordAsync(Password, "a brand new password");
        vault.Lock();
        var window = new VaultUnlockWindow(vault, Biometric);

        window.Show();
        await Settle(() => Texts(window).Any(t => t.Contains("master password was changed")));

        Assert.True(vault.IsLocked);
        Assert.DoesNotContain(Buttons(window), b => b.IsVisible && b.Content as string == "Use Test Hello");
        Assert.False(Biometric.IsOn(vault));
        Assert.Equal(1, _hello.Prompts); // Only the one that turned it on.
        window.Close();
    }

    /// <summary>Turns it on, then has a copy of Orrery that knows nothing of it change the password: what's left is found only by path.</summary>
    async Task<VaultFileStore> VaultWithLeftoverAsync()
    {
        using (var vault = await NewVaultAsync())
        {
            await Biometric.TurnOnAsync(vault, Password);
        }

        using (var elsewhere = new VaultFileStore(Location.FileIn("vault.frv"), new VaultFileStore.KdfSettings(8 * 1024, 1)))
        {
            await elsewhere.ChangePasswordAsync(Password, "a brand new password");
        }

        return new VaultFileStore(Location.FileIn("vault.frv"), new VaultFileStore.KdfSettings(8 * 1024, 1));
    }

    [AvaloniaFact]
    public async Task A_setup_found_only_by_the_vaults_location_is_described_as_either_and_not_removed()
    {
        using var vault = await VaultWithLeftoverAsync();
        var window = new VaultUnlockWindow(vault, Biometric);

        window.Show();
        await Settle(() => Texts(window).Any(t => t.Contains("used to be at this location")));

        Assert.DoesNotContain(Texts(window), t => t.Contains("The master password was changed"));
        Assert.Contains(Texts(window), t => t.Contains("before another copy of Orrery changed its master password"));
        Assert.True(Biometric.HasLeftovers(vault));
        Assert.Equal(0, _hello.Deleted);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Settings_removes_an_old_setup_after_saying_what_it_will_stop_opening()
    {
        using var vault = await VaultWithLeftoverAsync();
        var view = new SettingsView(Location, AppSettingsStore.In(Location), vault, Nothing, Nothing, Nothing, biometric: Biometric);
        var window = Show(view);
        await view.BiometricChecked;
        view.ShowPage("Data and security");

        var remove = Buttons(window).Single(b => b.Content as string == "Remove old Test Hello setup");
        Assert.True(remove.IsEffectivelyVisible);
        Assert.Equal("Remove old Test Hello setup", Avalonia.Automation.AutomationProperties.GetName(remove));
        Assert.Contains(Texts(window), t => t.Contains("used to be at this location"));
        bool finished = false;
        view.BiometricLeftoversRemoved += (_, _) => finished = true;

        // Canceling the confirmation keeps it.
        remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Settle(() => window.OwnedWindows.Count == 1);
        var confirm = window.OwnedWindows.Single();
        Assert.Contains(Texts(confirm), t => t.Contains("won't open with Test Hello any more"));
        Buttons(confirm).Single(b => b.Content as string == "Cancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Settle(() => finished);
        Assert.True(Biometric.HasLeftovers(vault));
        Assert.Equal(0, _hello.Deleted);

        finished = false;
        remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Settle(() => window.OwnedWindows.Count == 1);
        Buttons(window.OwnedWindows.Single()).Single(b => b.Content as string == "Remove").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Settle(() => finished);

        Assert.False(Biometric.HasLeftovers(vault));
        Assert.DoesNotContain(_records.Items.Keys, k => k.StartsWith(BiometricUnlock.RecordPrefix));
        Assert.Equal(1, _hello.Deleted);
        Assert.False(remove.IsEffectivelyVisible);
        Assert.Contains(Texts(window), t => t.Contains("old Test Hello setup was removed"));
        AccessibilityAudit.AssertAllNamed(window);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Settings_offers_no_removal_when_nothing_is_left_over()
    {
        using var vault = await NewVaultAsync();
        await Biometric.TurnOnAsync(vault, Password);
        var view = new SettingsView(Location, AppSettingsStore.In(Location), vault, Nothing, Nothing, Nothing, biometric: Biometric);
        var window = Show(view);
        await view.BiometricChecked;

        Assert.False(Buttons(window).Single(b => b.Content as string == "Remove old Test Hello setup").IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Without_it_turned_on_the_unlock_window_only_asks_for_the_password()
    {
        using var vault = await NewVaultAsync();
        var window = new VaultUnlockWindow(vault, Biometric);

        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, _hello.Prompts);
        Assert.DoesNotContain(Buttons(window), b => b.IsVisible && b.Content as string == "Use Test Hello");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Settings_has_no_switch_where_the_system_has_no_authenticator()
    {
        using var vault = await NewVaultAsync();
        var window = Show(new SettingsView(Location, AppSettingsStore.In(Location), vault, Nothing, Nothing, Nothing));

        Assert.DoesNotContain(window.GetVisualDescendants().OfType<CheckBox>(), c => (c.Content as string)?.StartsWith("Unlock with") == true);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Settings_disables_the_switch_and_says_why_when_it_cant_be_used()
    {
        using var vault = await NewVaultAsync();
        _hello.Unavailable = "Test Hello isn't set up.";
        var view = new SettingsView(Location, AppSettingsStore.In(Location), vault, Nothing, Nothing, Nothing, biometric: Biometric);
        var window = Show(view);
        await view.BiometricChecked;

        var toggle = Switch(window);
        Assert.False(toggle.IsEnabled);
        Assert.Contains("Test Hello isn't set up.", Texts(window));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Turning_the_switch_off_deletes_the_saved_key()
    {
        using var vault = await NewVaultAsync();
        await Biometric.TurnOnAsync(vault, Password);
        var view = new SettingsView(Location, AppSettingsStore.In(Location), vault, Nothing, Nothing, Nothing, biometric: Biometric);
        var window = Show(view);
        await view.BiometricChecked;

        var toggle = Switch(window);
        Assert.True(toggle.IsEnabled);
        Assert.True(toggle.IsChecked);

        toggle.IsChecked = false;
        await Settle(() => !Biometric.IsOn(vault));

        Assert.Empty(_records.Items);
        Assert.Equal(1, _hello.Deleted);
        Assert.Contains(Texts(window), t => t.Contains("its key was deleted"));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Settings_turns_off_a_key_left_stale_by_a_password_change_elsewhere()
    {
        using var vault = await NewVaultAsync();
        await Biometric.TurnOnAsync(vault, Password);
        await vault.ChangePasswordAsync(Password, "a brand new password");
        var view = new SettingsView(Location, AppSettingsStore.In(Location), vault, Nothing, Nothing, Nothing, biometric: Biometric);
        var window = Show(view);
        await view.BiometricChecked;

        Assert.False(Switch(window).IsChecked);
        Assert.Contains(Texts(window), t => t.Contains("master password was changed"));
        Assert.False(Biometric.IsOn(vault));
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_setup_window_checks_the_master_password_then_turns_it_on()
    {
        using var vault = await NewVaultAsync();
        var window = new BiometricSetupWindow(vault, Biometric);
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        window.Show();
        var password = window.GetVisualDescendants().OfType<TextBox>().Single();
        var turnOn = Buttons(window).Single(b => b.Content as string == "Turn on");

        password.Text = "not it";
        turnOn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Settle(() => Texts(window).Contains("That password is incorrect."));
        Assert.False(Biometric.IsOn(vault));
        Assert.Equal(0, _hello.Prompts);

        password.Text = Password;
        turnOn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Settle(() => closed);
        Assert.True(Biometric.IsOn(vault));
        Assert.True(vault.IsLocked);
    }

    // ---- Cancel, one prompt at a time, remote sessions ----

    [AvaloniaFact]
    public async Task Cancel_dismisses_the_prompt_and_leaves_the_vault_locked()
    {
        using var vault = await NewVaultAsync();
        await Biometric.TurnOnAsync(vault, Password);
        _hello.Hold = new TaskCompletionSource();
        _hello.HonorsCancel = false; // Even if the prompt answers yes just after.
        var window = new VaultUnlockWindow(vault, Biometric);
        window.Show();
        await Settle(() => _hello.Prompts == 2);

        var cancel = Buttons(window).Single(b => b.Content as string == "Cancel");
        Assert.True(cancel.IsEnabled);
        cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(window.IsVisible);

        _hello.Hold.SetResult();
        await Settle(() => _hello.SawCancel);
        for (int i = 0; i < 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Assert.True(vault.IsLocked);
    }

    [AvaloniaFact]
    public async Task Closing_the_window_cancels_the_prompt()
    {
        using var vault = await NewVaultAsync();
        await Biometric.TurnOnAsync(vault, Password);
        _hello.Hold = new TaskCompletionSource();
        var window = new VaultUnlockWindow(vault, Biometric);
        window.Show();
        await Settle(() => _hello.Prompts == 2);

        window.Close();

        await Settle(() => _hello.SawCancel);
        Assert.True(vault.IsLocked);
    }

    [AvaloniaFact]
    public async Task Several_callers_share_one_unlock_window_and_one_prompt()
    {
        using var vault = await NewVaultAsync();
        var biometric = Biometric;
        await biometric.TurnOnAsync(vault, Password);
        _hello.Hold = new TaskCompletionSource();
        var owner = new Window();
        owner.Show();

        var first = VaultUnlockWindow.EnsureUnlockedAsync(vault, owner, biometric);
        var second = VaultUnlockWindow.EnsureUnlockedAsync(vault, owner, biometric);
        await Settle(() => _hello.Prompts == 2);

        Assert.Single(owner.OwnedWindows);
        _hello.Hold.SetResult();
        await Settle(() => first.IsCompleted && second.IsCompleted);
        Assert.True(await first);
        Assert.True(await second);
        Assert.Equal(2, _hello.Prompts);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task Callers_waiting_on_a_window_canceled_just_as_the_prompt_opened_the_vault_get_no()
    {
        using var vault = await NewVaultAsync();
        var biometric = Biometric;
        await biometric.TurnOnAsync(vault, Password);
        _hello.Hold = new TaskCompletionSource();
        _hello.HonorsCancel = false;
        var owner = new Window();
        owner.Show();

        var first = VaultUnlockWindow.EnsureUnlockedAsync(vault, owner, biometric);
        var second = VaultUnlockWindow.EnsureUnlockedAsync(vault, owner, biometric);
        await Settle(() => _hello.Prompts == 2);
        var window = owner.OwnedWindows.Single();

        // The prompt's answer has opened the vault, but the window hasn't heard yet when the user clicks Cancel.
        await vault.UnlockAsync(Password);
        bool lockedWhenClosed = false;
        window.Closed += (_, _) => lockedWhenClosed = vault.IsLocked;
        Buttons(window).Single(b => b.Content as string == "Cancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.True(lockedWhenClosed); // Before anyone waiting on the window resumes.
        _hello.Hold.SetResult();
        await Settle(() => first.IsCompleted && second.IsCompleted);
        Assert.False(await first);
        Assert.False(await second);
        Assert.True(vault.IsLocked);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task In_a_remote_session_the_unlock_window_asks_only_for_the_password_and_says_why()
    {
        using var vault = await NewVaultAsync();
        await Biometric.TurnOnAsync(vault, Password);
        _remote.IsRemote = true;
        var window = new VaultUnlockWindow(vault, Biometric);

        window.Show();
        await Settle(() => Texts(window).Any(t => t.Contains("remote session")));

        Assert.Equal(1, _hello.Prompts); // Only the one that turned it on.
        Assert.False(Buttons(window).Single(b => b.Content as string == "Use Test Hello").IsVisible);
        Assert.True(Biometric.IsOn(vault));
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_key_windows_cant_find_isnt_deleted_by_the_unlock_window()
    {
        using var vault = await NewVaultAsync();
        await Biometric.TurnOnAsync(vault, Password);
        _hello.NextAnswer = AuthenticatorStatus.KeyGone;
        var window = new VaultUnlockWindow(vault, Biometric);

        window.Show();
        await Settle(() => Texts(window).Any(t => t.Contains("off and on again in Settings")));

        Assert.Equal(0, _hello.Deleted);
        Assert.True(Biometric.IsOn(vault));
        window.Close();
    }

    // ---- Settings ----

    [AvaloniaFact]
    public async Task The_switch_is_rechecked_after_every_change()
    {
        using var vault = await NewVaultAsync();
        await Biometric.TurnOnAsync(vault, Password);
        _remote.IsRemote = true;
        var view = new SettingsView(Location, AppSettingsStore.In(Location), vault, Nothing, Nothing, Nothing, biometric: Biometric);
        var window = Show(view);
        await view.BiometricChecked;

        // On, so it can be turned off even here; once off, it can't be turned on again from a remote session.
        var toggle = Switch(window);
        Assert.True(toggle.IsEnabled);
        bool toggled = false;
        view.BiometricToggled += (_, _) => toggled = true;
        toggle.IsChecked = false;
        await Settle(() => toggled);

        Assert.False(toggle.IsChecked);
        Assert.False(toggle.IsEnabled);
        Assert.Contains(Texts(window), t => t.Contains("remote session"));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Changing_the_master_password_in_settings_turns_it_off_and_says_so()
    {
        using var vault = await NewVaultAsync();
        await Biometric.TurnOnAsync(vault, Password);
        var view = new SettingsView(Location, AppSettingsStore.In(Location), vault, Nothing, Nothing, Nothing, biometric: Biometric);
        var window = Show(view);
        await view.BiometricChecked;
        Assert.True(Switch(window).IsChecked);

        Buttons(window).Single(b => b.Content as string == "Change master password…").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Settle(() => window.OwnedWindows.Count == 1);
        var change = window.OwnedWindows.Single();
        var fields = change.GetVisualDescendants().OfType<Orrery.App.Controls.PasswordBox>().ToList();
        fields[0].Text = Password;
        fields[1].Text = fields[2].Text = "a brand new password";
        Buttons(change).Single(b => b.Content as string == "Change password").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        await Settle(() => window.OwnedWindows.Count == 1 && window.OwnedWindows[0] != change);
        var message = window.OwnedWindows.Single();
        Assert.Contains(Texts(message), t => t.Contains("Test Hello unlock was turned off"));
        message.Close();

        Assert.False(Switch(window).IsChecked);
        Assert.Contains(Texts(window), t => t.Contains("turned off because the master password changed"));
        Assert.DoesNotContain(_records.Items.Keys, k => k.StartsWith(BiometricUnlock.RecordPrefix));
        Assert.Equal(2, _hello.Deleted); // The old key, and the new password's name in case it was there.
        window.Close();
    }

    [AvaloniaFact]
    public async Task Switching_to_the_system_keychain_turns_it_off_for_the_vault_left_behind()
    {
        using var vault = await NewVaultAsync();
        await Biometric.TurnOnAsync(vault, Password);
        var view = new SettingsView(Location, AppSettingsStore.In(Location), vault, Nothing, Nothing, Nothing, biometric: Biometric);
        var window = Show(view);
        await view.BiometricChecked;

        // The step the store switch takes once the credentials are copied.
        await view.TurnOffBiometricForOldVaultAsync();

        Assert.False(Biometric.IsOn(vault));
        Assert.Empty(_records.Items);
        Assert.Equal(1, _hello.Deleted);
        window.Close();
    }

    // ---- The setup window ----

    [AvaloniaFact]
    public async Task The_setup_window_says_what_opens_the_vault_before_it_asks()
    {
        using var vault = await NewVaultAsync();
        var window = new BiometricSetupWindow(vault, Biometric);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var notice = window.GetVisualDescendants().OfType<Orrery.App.Controls.Notice>().Single(n => n.IsVisible);
        Assert.Contains("PIN", notice.Text);
        Assert.Contains("any face or fingerprint", notice.Text);
        Assert.Contains("doesn't turn this off", notice.Text);
        Assert.Equal(0, _hello.Prompts);
        window.Close();
    }

    [AvaloniaFact]
    public async Task After_a_refusal_the_setup_window_says_why_before_asking_again()
    {
        using var vault = await NewVaultAsync();
        var biometric = Biometric;
        _hello.NextAnswer = AuthenticatorStatus.Failed;
        Assert.Equal(BiometricOutcome.Failed, (await biometric.TurnOnAsync(vault, Password)).Outcome);
        Assert.NotNull(biometric.LastTurnOnProblem);

        var window = new BiometricSetupWindow(vault, biometric);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.ErrorNotice.IsVisible);
        Assert.StartsWith("Last time: ", window.ErrorNotice.Text);
        Assert.Equal("Try again", window.OkButton.Content);
        window.Close();

        // Once it works, nothing is remembered.
        Assert.Equal(BiometricOutcome.Done, (await biometric.TurnOnAsync(vault, Password)).Outcome);
        Assert.Null(biometric.LastTurnOnProblem);
        window = new BiometricSetupWindow(vault, biometric);
        window.Show();
        Assert.False(window.ErrorNotice.IsVisible);
        Assert.Equal("Turn on", window.OkButton.Content);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_setup_window_hides_the_password_from_ui_automation_and_names_everything()
    {
        using var vault = await NewVaultAsync();
        var window = new BiometricSetupWindow(vault, Biometric);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Single(window.GetVisualDescendants().OfType<Orrery.App.Controls.PasswordBox>());
        AccessibilityAudit.AssertAllNamed(window);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_unlock_window_with_it_on_names_everything()
    {
        using var vault = await NewVaultAsync();
        await Biometric.TurnOnAsync(vault, Password);
        _hello.NextAnswer = AuthenticatorStatus.Canceled;
        var window = new VaultUnlockWindow(vault, Biometric);
        window.Show();
        await Settle(() => _hello.Prompts == 2);

        AccessibilityAudit.AssertAllNamed(window);
        window.Close();
    }

    static Task Nothing() => Task.CompletedTask;

    static Window Show(Control view)
    {
        var window = new Window { Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    static CheckBox Switch(Window window) =>
        window.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Content as string == "Unlock with Test Hello");

    static IEnumerable<Button> Buttons(Window window) => window.GetVisualDescendants().OfType<Button>();

    static List<string> Texts(Window window) =>
        window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsVisible && t.Text is not null).Select(t => t.Text!).ToList();

    /// <summary>Lets the vault's background work and the dispatcher run until <paramref name="done"/>.</summary>
    static async Task Settle(Func<bool> done)
    {
        for (int i = 0; i < 500 && !done(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Assert.True(done());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}

/// <summary>Stands in for Windows Hello: an HMAC of the challenge under a key only it holds. Never touches the real one.</summary>
sealed class TestAuthenticator : IPlatformAuthenticator
{
    readonly Dictionary<string, byte[]> _keys = [];

    public string Name => "Test Hello";

    public string Id => "test-hello";

    public string? Unavailable { get; set; }

    public AuthenticatorStatus? NextAnswer { get; set; }

    /// <summary>While set, the prompt stays up until this completes; canceling only gives up when <see cref="HonorsCancel"/>.</summary>
    public TaskCompletionSource? Hold { get; set; }

    public bool HonorsCancel { get; set; } = true;

    public bool SawCancel { get; private set; }

    public int Prompts { get; private set; }

    public int Deleted { get; private set; }

    public Task<string?> CheckAvailableAsync() => Task.FromResult(Unavailable);

    public async Task<AuthenticatorResult> CreateAsync(string keyName, byte[] challenge, nint window, CancellationToken cancel)
    {
        _keys[keyName] = RandomNumberGenerator.GetBytes(32);
        return (await ReleaseAsync(keyName, challenge, window, cancel)) with { HardwareBacked = true };
    }

    public async Task<AuthenticatorResult> ReleaseAsync(string keyName, byte[] challenge, nint window, CancellationToken cancel)
    {
        Prompts++;
        if (Hold is { } hold)
        {
            try
            {
                await (HonorsCancel ? hold.Task.WaitAsync(cancel) : hold.Task);
            }
            catch (OperationCanceledException)
            {
                SawCancel = true;
                return new AuthenticatorResult(AuthenticatorStatus.Canceled);
            }

            SawCancel |= cancel.IsCancellationRequested;
        }

        if (NextAnswer is { } answer)
        {
            NextAnswer = null;
            return new AuthenticatorResult(answer);
        }

        if (!_keys.TryGetValue(keyName, out var key))
        {
            return new AuthenticatorResult(AuthenticatorStatus.KeyGone);
        }

        Secrets.Add(HMACSHA256.HashData(key, challenge));
        return AuthenticatorResult.Released(Secrets[^1].ToArray());
    }

    /// <summary>Every secret handed out, to check none reaches a file or the log.</summary>
    public List<byte[]> Secrets { get; } = [];

    public Task DeleteAsync(string keyName)
    {
        _keys.Remove(keyName);
        Deleted++;
        return Task.CompletedTask;
    }
}

sealed class MemorySecrets : ISecretBackend
{
    public Dictionary<string, byte[]> Items { get; } = [];

    public string Name => "test keychain";

    public int? MaxSecretSize => null;

    public void Write(string key, byte[] secret) => Items[key] = secret.ToArray();

    public byte[]? Read(string key) => Items.TryGetValue(key, out var secret) ? secret.ToArray() : null;

    public void Delete(string key) => Items.Remove(key);

    public IReadOnlyList<string> List(string prefix) => Items.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
}

sealed class TestRemoteSession : IRemoteSession
{
    public bool IsRemote { get; set; }
}
