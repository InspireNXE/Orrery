using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Orrery.Core.Credentials;

namespace Orrery.App.Credentials;

/// <summary>
/// Turns on Windows Hello or Touch ID unlock: asks for the master password once, then shows the system prompt.
/// Closes with true once it's on.
/// </summary>
sealed class BiometricSetupWindow : Window
{
    readonly VaultFileStore _vault;
    readonly BiometricUnlock _biometric;
    readonly TextBox _password = new() { PasswordChar = '•' };
    readonly TextBlock _error = new() { Foreground = Dialogs.DangerBrush, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    readonly Button _ok;

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
        _ok.Click += async (_, _) => await SubmitAsync();
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(16, 6) };
        cancel.Click += (_, _) => Close(false);

        Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = $"Unlock the vault with {biometric.Name}", FontSize = 18, FontWeight = FontWeight.SemiBold },
                new TextBlock
                {
                    Text = $"Enter the master password once, then confirm with {biometric.Name}. After that, {biometric.Name} opens the vault on this computer. "
                        + "The master password still works.",
                    TextWrapping = TextWrapping.Wrap,
                },
                Dialogs.Field("Master password", _password),
                _error,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, _ok } },
            },
        };

        Opened += (_, _) => _password.Focus();
    }

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
        try
        {
            var result = await _biometric.TurnOnAsync(_vault, password, TryGetPlatformHandle()?.Handle ?? 0);
            if (result.Outcome == BiometricOutcome.Done)
            {
                Close(true);
                return;
            }

            ShowError(result.Outcome == BiometricOutcome.Cancelled
                ? $"{_biometric.Name} was cancelled, so nothing changed."
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
            _ok.IsEnabled = true;
            _ok.Content = "Turn on";
        }
    }

    void ShowError(string message)
    {
        _error.Text = message;
        _error.IsVisible = true;
    }
}
