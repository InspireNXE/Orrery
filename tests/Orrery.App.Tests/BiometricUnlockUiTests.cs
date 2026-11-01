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

    BiometricUnlock Biometric => new(_hello, _records);

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
    public async Task Cancelling_the_prompt_leaves_the_password_field_and_the_button()
    {
        using var vault = await NewVaultAsync();
        await Biometric.TurnOnAsync(vault, Password);
        _hello.NextAnswer = AuthenticatorStatus.Cancelled;
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
        Assert.False(Buttons(window).Single(b => b.Content as string == "Use Test Hello").IsVisible);
        Assert.False(Biometric.IsOn(vault));
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
        var view = new SettingsView(Location, AppSettingsStore.In(Location), vault, Nothing, Nothing, Nothing, Biometric);
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
        var view = new SettingsView(Location, AppSettingsStore.In(Location), vault, Nothing, Nothing, Nothing, Biometric);
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
        var view = new SettingsView(Location, AppSettingsStore.In(Location), vault, Nothing, Nothing, Nothing, Biometric);
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

/// <summary>Stands in for Windows Hello: an HMAC of the challenge under a key only it holds.</summary>
sealed class TestAuthenticator : IPlatformAuthenticator
{
    readonly Dictionary<string, byte[]> _keys = [];

    public string Name => "Test Hello";

    public string Id => "test-hello";

    public string? Unavailable { get; set; }

    public AuthenticatorStatus? NextAnswer { get; set; }

    public int Prompts { get; private set; }

    public int Deleted { get; private set; }

    public Task<string?> CheckAvailableAsync() => Task.FromResult(Unavailable);

    public Task<AuthenticatorResult> CreateAsync(string keyName, byte[] challenge, nint window)
    {
        _keys[keyName] = RandomNumberGenerator.GetBytes(32);
        return ReleaseAsync(keyName, challenge, window);
    }

    public Task<AuthenticatorResult> ReleaseAsync(string keyName, byte[] challenge, nint window)
    {
        Prompts++;
        if (NextAnswer is { } answer)
        {
            NextAnswer = null;
            return Task.FromResult(new AuthenticatorResult(answer));
        }

        if (!_keys.TryGetValue(keyName, out var key))
        {
            return Task.FromResult(new AuthenticatorResult(AuthenticatorStatus.KeyGone));
        }

        Secrets.Add(HMACSHA256.HashData(key, challenge));
        return Task.FromResult(AuthenticatorResult.Released(Secrets[^1].ToArray()));
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
}
