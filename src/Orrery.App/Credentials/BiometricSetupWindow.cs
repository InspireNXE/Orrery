using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Orrery.App.Controls;
using Orrery.Core.Credentials;

namespace Orrery.App.Credentials;

/// <summary>
/// Turns on Windows Hello or Touch ID unlock: says what will open the vault from now on, asks for the master password
/// once, then shows the system prompt. Closes with true once it's on.
/// </summary>
sealed class BiometricSetupWindow : Window
{
    readonly VaultFileStore _vault;
    readonly BiometricUnlock _biometric;
    readonly TextBox _password = new PasswordBox();
    readonly Notice _error = new(NoticeKind.Error) { IsVisible = false };
    readonly Button _ok;
    CancellationTokenSource? _prompt;

    public BiometricSetupWindow(VaultFileStore vault, BiometricUnlock biometric)
    {
        _vault = vault;
        _biometric = biometric;
        Title = $"Turn on {biometric.Name} unlock";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Dialogs.PanelBrush;

        _ok = new Button { Content = "Turn on", IsDefault = true, Padding = new Thickness(16, 6) };
        _ok.Classes.Add("accent");
        AutomationProperties.SetName(_ok, $"Turn on {biometric.Name} unlock");
        _ok.Click += async (_, _) => await SubmitAsync();
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(16, 6) };
        cancel.Click += (_, _) => Close(false);

        var trade = new Notice(NoticeKind.Warning, TradeOff(biometric.Name));

        // The last try's answer, said first: trying again without fixing it shows the prompt only to fail the same way.
        if (biometric.LastTurnOnProblem is { } last)
        {
            _error.Text = $"Last time: {last}";
            _error.IsVisible = true;
            _ok.Content = TryAgain;
        }

        Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 12,
            Children =
            {
                Dialogs.Heading(new TextBlock { Text = $"Unlock the vault with {biometric.Name}", FontSize = 18, FontWeight = FontWeight.SemiBold }),
                new TextBlock
                {
                    Text = $"Enter the master password once, then confirm with {biometric.Name}. After that, {biometric.Name} opens the vault on this computer. "
                        + "The master password still works.",
                    TextWrapping = TextWrapping.Wrap,
                },
                trade,
                Dialogs.Field("Master password", _password),
                _error,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, _ok } },
            },
        };

        Opened += (_, _) => _password.Focus();
        Closed += (_, _) => _prompt?.Cancel();
    }

    const string TryAgain = "Try again";

    internal Notice ErrorNotice => _error;
    internal Button OkButton => _ok;

    /// <summary>What will open the vault once this is on, said before the user agrees.</summary>
    internal static string TradeOff(string name) => OperatingSystem.IsMacOS()
        ? $"Only the fingers enrolled in {name} now will open the vault. Adding or removing a finger stops it working until you turn it on again."
        : $"Your {name} PIN, and any face or fingerprint set up in {name} on this Windows account, will open the vault on this computer. "
            + "Changing the PIN or adding a face or finger doesn't turn this off. To stop it, turn it off in Settings or change the master password.";

    async Task SubmitAsync()
    {
        var password = _password.Text ?? string.Empty;
        if (password.Length == 0)
        {
            ShowError("Enter the master password.");
            return;
        }

        _ok.IsEnabled = false;
        _ok.Content = "Turning on…";
        _error.IsVisible = false;
        using var prompt = new CancellationTokenSource();
        _prompt = prompt;
        try
        {
            var result = await _biometric.TurnOnAsync(_vault, password, TryGetPlatformHandle()?.Handle ?? 0, prompt.Token);
            if (prompt.IsCancellationRequested)
            {
                // Closed while the prompt was up: leave it off.
                if (result.Outcome == BiometricOutcome.Done)
                {
                    await _biometric.TurnOffAsync(_vault);
                }

                return;
            }

            if (result.Outcome == BiometricOutcome.Done)
            {
                Close(true);
                return;
            }

            ShowError(result.Outcome == BiometricOutcome.Canceled
                ? $"{_biometric.Name} was canceled, so nothing changed."
                : result.Message ?? $"{_biometric.Name} didn't work, so nothing changed.");
        }
        catch (WrongVaultPasswordException)
        {
            ShowError("That password is incorrect.");
            _password.Text = string.Empty;
            _password.Focus();
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            ShowError($"Nothing changed. {e.Message}");
        }
        finally
        {
            _prompt = null;
            _ok.IsEnabled = true;
            _ok.Content = _biometric.LastTurnOnProblem is null ? "Turn on" : TryAgain;
        }
    }

    void ShowError(string message)
    {
        _error.Text = message;
        _error.IsVisible = true;
    }
}
