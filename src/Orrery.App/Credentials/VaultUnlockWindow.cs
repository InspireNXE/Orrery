using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Orrery.Core.Credentials;

namespace Orrery.App.Credentials;

/// <summary>
/// Creates the vault on first use, or unlocks it. Closes with true once the store is open. When Windows Hello or
/// Touch ID unlock is on, its prompt shows as the window opens, with the password field below it.
/// </summary>
sealed class VaultUnlockWindow : Window
{
    const int MinimumPasswordLength = 8;

    static readonly IBrush MutedBrush = new SolidColorBrush(Color.Parse("#9AA3B2"));
    static readonly IBrush WarningBrush = new SolidColorBrush(Color.Parse("#F3B672"));
    static readonly IBrush DangerBrush = new SolidColorBrush(Color.Parse("#FF9AA2"));

    readonly ICredentialStore _store;
    readonly bool _creating;
    readonly BiometricUnlock? _biometric;
    readonly TextBox _password = new() { PasswordChar = '•' };
    readonly TextBox _confirm = new() { PasswordChar = '•' };
    readonly TextBlock _notice = new() { Foreground = WarningBrush, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    readonly TextBlock _error = new() { Foreground = DangerBrush, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    readonly Button _ok;
    readonly Button _useBiometric = new() { Padding = new Thickness(16, 6), IsVisible = false };

    /// <summary>Windows Hello or Touch ID, where this system has one: what <see cref="EnsureUnlockedAsync"/> offers.</summary>
    public static BiometricUnlock? Biometric { get; set; } = BiometricUnlock.ForThisSystem();

    public VaultUnlockWindow(ICredentialStore store, BiometricUnlock? biometric = null)
    {
        _store = store;
        _creating = !store.Exists;
        _biometric = !_creating && store is VaultFileStore vault && biometric?.IsOn(vault) == true ? biometric : null;

        Title = _creating ? "Create vault" : "Unlock vault";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#14161B"));

        _ok = new Button { Content = _creating ? "Create vault" : "Unlock", IsDefault = true, Padding = new Thickness(16, 6) };
        _ok.Classes.Add("accent");
        _ok.Click += async (_, _) => await SubmitAsync();
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(16, 6) };
        cancel.Click += (_, _) => Close(false);
        if (_biometric is not null)
        {
            _useBiometric.Content = $"Use {_biometric.Name}";
            _useBiometric.IsVisible = true;
            _useBiometric.Click += async (_, _) => await UseBiometricAsync();
        }

        var body = new StackPanel { Spacing = 12, Margin = new Thickness(24) };
        body.Children.Add(new TextBlock
        {
            Text = _creating ? "Create a password vault" : "Unlock your password vault",
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
        });
        body.Children.Add(new TextBlock
        {
            Text = _creating
                ? "Saved passwords are encrypted with a master password you choose. Orrery never stores it."
                : _biometric is not null
                    ? $"Use {_biometric.Name} or enter your master password to use saved credentials."
                    : "Enter your master password to use saved credentials.",
            TextWrapping = TextWrapping.Wrap,
        });

        if (_creating)
        {
            body.Children.Add(new TextBlock
            {
                Text = "If you forget the master password, there is no way to recover the vault or anything in it.",
                Foreground = WarningBrush,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        body.Children.Add(Field("Master password", _password));
        if (_creating)
        {
            body.Children.Add(Field("Confirm master password", _confirm));
        }

        body.Children.Add(_notice);
        body.Children.Add(_error);
        DockPanel.SetDock(_useBiometric, Dock.Left);
        body.Children.Add(new DockPanel
        {
            Children =
            {
                _useBiometric,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancel, _ok },
                },
            },
        });

        Content = body;
        Opened += async (_, _) =>
        {
            _password.Focus();
            await UseBiometricAsync();
        };
    }

    static Control Field(string label, Control input) => new StackPanel
    {
        Spacing = 4,
        Children = { new TextBlock { Text = label, FontSize = 12, Foreground = MutedBrush }, input },
    };

    /// <summary>
    /// Shows the system prompt. Cancelling it leaves the password field; a key that can't work any more is turned off
    /// and the reason shown.
    /// </summary>
    async Task UseBiometricAsync()
    {
        if (_biometric is null || _store is not VaultFileStore vault || !_useBiometric.IsVisible || !_useBiometric.IsEnabled)
        {
            return;
        }

        _useBiometric.IsEnabled = _ok.IsEnabled = false;
        _error.IsVisible = false;
        try
        {
            var result = await _biometric.UnlockAsync(vault, TryGetPlatformHandle()?.Handle ?? 0);
            switch (result.Outcome)
            {
                case BiometricOutcome.Done:
                    Close(true);
                    return;
                case BiometricOutcome.TurnedOff or BiometricOutcome.NotOn:
                    _useBiometric.IsVisible = false;
                    _notice.Text = result.Message;
                    _notice.IsVisible = result.Message is not null;
                    break;
                case BiometricOutcome.Failed:
                    ShowError(result.Message ?? $"{_biometric.Name} didn't work. Use the master password.");
                    break;
            }
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            ShowError(e.Message);
        }
        finally
        {
            _useBiometric.IsEnabled = _ok.IsEnabled = true;
        }

        _password.Focus();
    }

    async Task SubmitAsync()
    {
        var password = _password.Text ?? string.Empty;
        if (_creating)
        {
            if (password.Length < MinimumPasswordLength)
            {
                ShowError($"Use at least {MinimumPasswordLength} characters. A short phrase is easier to remember than a complex word.");
                return;
            }

            if (password != _confirm.Text)
            {
                ShowError("The passwords don't match.");
                return;
            }
        }
        else if (password.Length == 0)
        {
            return;
        }

        _ok.IsEnabled = false;
        _ok.Content = _creating ? "Creating…" : "Unlocking…";
        try
        {
            await _store.UnlockAsync(password);
            Close(true);
        }
        catch (WrongVaultPasswordException)
        {
            ShowError("That password is incorrect.");
            _password.Text = string.Empty;
            _password.Focus();
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            ShowError(e.Message);
        }
        finally
        {
            _ok.IsEnabled = true;
            _ok.Content = _creating ? "Create vault" : "Unlock";
        }
    }

    void ShowError(string message)
    {
        _error.Text = message;
        _error.IsVisible = true;
    }

    /// <summary>Unlocks the store if needed. False if the user cancelled.</summary>
    public static async Task<bool> EnsureUnlockedAsync(ICredentialStore store, Window owner) =>
        !store.IsLocked || await new VaultUnlockWindow(store, Biometric).ShowDialog<bool>(owner);
}
