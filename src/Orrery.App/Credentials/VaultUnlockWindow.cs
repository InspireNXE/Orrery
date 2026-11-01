using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Orrery.App.Controls;
using Orrery.Core.Credentials;

namespace Orrery.App.Credentials;

/// <summary>
/// Creates the vault on first use, or unlocks it. Closes with true once the store is open. When Windows Hello or
/// Touch ID unlock is on, its prompt shows as the window opens, with the password field below it.
/// </summary>
sealed class VaultUnlockWindow : Window
{
    const int MinimumPasswordLength = 8;

    readonly ICredentialStore _store;
    readonly bool _creating;
    readonly BiometricUnlock? _biometric;
    readonly TextBox _password = new PasswordBox();
    readonly TextBox _confirm = new PasswordBox();
    readonly Notice _notice = new(NoticeKind.Warning) { IsVisible = false };
    readonly Notice _error = new(NoticeKind.Error) { IsVisible = false };
    readonly Button _ok;
    readonly Button _useBiometric = new() { Padding = new Thickness(16, 6), IsVisible = false };
    CancellationTokenSource? _prompt;
    bool _closed;

    // The unlock window open for each store, so several callers asking at once share one window and one prompt.
    static readonly Dictionary<ICredentialStore, Task<bool>> OpenWindows = [];

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
        Background = Dialogs.PanelBrush;

        _ok = new Button { Content = _creating ? "Create vault" : "Unlock", IsDefault = true, Padding = new Thickness(16, 6) };
        _ok.Classes.Add("accent");
        _ok.Click += async (_, _) => await SubmitAsync();
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(16, 6) };
        cancel.Click += (_, _) => Close(false);
        if (_biometric is not null)
        {
            _useBiometric.Content = $"Use {_biometric.Name}";
            AutomationProperties.SetName(_useBiometric, $"Use {_biometric.Name}");
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
            body.Children.Add(new Notice(NoticeKind.Warning, "If you forget the master password, there is no way to recover the vault or anything in it."));
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
            if (_biometric is null && !_creating && biometric is not null && store is VaultFileStore staleVault)
            {
                // Not on for the vault as it is now; say so if that's because a saved key went stale.
                await ShowIfTurnedOffAsync(biometric, staleVault);
            }

            await UseBiometricAsync();
        };

        // Closing the window (Cancel, Escape, the close box) dismisses a prompt that's still showing.
        Closed += (_, _) =>
        {
            _closed = true;
            _prompt?.Cancel();
        };
    }

    static Control Field(string label, Control input) => Dialogs.Field(label, input);

    async Task ShowIfTurnedOffAsync(BiometricUnlock biometric, VaultFileStore vault)
    {
        if (await biometric.TurnOffIfStaleAsync(vault) is { } message && !_closed)
        {
            _notice.Kind = NoticeKind.Warning;
            _notice.Text = message;
            _notice.IsVisible = true;
        }
    }

    /// <summary>
    /// Shows the system prompt. Cancelling it leaves the password field; a key that can't work any more is turned off
    /// and the reason shown. Cancel stays usable throughout and dismisses the prompt.
    /// </summary>
    async Task UseBiometricAsync()
    {
        if (_biometric is null || _store is not VaultFileStore vault || !_useBiometric.IsVisible || !_useBiometric.IsEnabled || _closed)
        {
            return;
        }

        _useBiometric.IsEnabled = _ok.IsEnabled = false;
        _error.IsVisible = false;
        using var prompt = new CancellationTokenSource();
        _prompt = prompt;
        try
        {
            var result = await _biometric.UnlockAsync(vault, TryGetPlatformHandle()?.Handle ?? 0, prompt.Token);
            if (_closed || prompt.IsCancellationRequested)
            {
                // Closed while the prompt was up: whatever it answered, the vault stays as the user left it.
                if (result.Outcome == BiometricOutcome.Done)
                {
                    vault.Lock();
                }

                return;
            }

            switch (result.Outcome)
            {
                case BiometricOutcome.Done:
                    Close(true);
                    return;
                case BiometricOutcome.TurnedOff or BiometricOutcome.NotOn or BiometricOutcome.Unavailable:
                    _useBiometric.IsVisible = false;
                    _notice.Kind = result.Outcome == BiometricOutcome.Unavailable ? NoticeKind.Info : NoticeKind.Warning;
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
            if (!_closed) ShowError(e.Message);
        }
        finally
        {
            _prompt = null;
            _useBiometric.IsEnabled = _ok.IsEnabled = true;
        }

        if (!_closed) _password.Focus();
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

    /// <summary>
    /// Unlocks the store if needed. False if the user cancelled. While an unlock window is already open for the
    /// store, waits for that one instead of opening a second (and a second Windows Hello prompt).
    /// </summary>
    public static Task<bool> EnsureUnlockedAsync(ICredentialStore store, Window owner) => EnsureUnlockedAsync(store, owner, Biometric);

    internal static async Task<bool> EnsureUnlockedAsync(ICredentialStore store, Window owner, BiometricUnlock? biometric)
    {
        if (!store.IsLocked)
        {
            return true;
        }

        if (OpenWindows.TryGetValue(store, out var open))
        {
            await open;
            return !store.IsLocked;
        }

        var shown = new VaultUnlockWindow(store, biometric).ShowDialog<bool>(owner);
        OpenWindows[store] = shown;
        try
        {
            return await shown && !store.IsLocked;
        }
        finally
        {
            OpenWindows.Remove(store);
        }
    }
}
