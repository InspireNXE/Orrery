using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Orrery.App.Controls;
using Orrery.Core.Credentials;

namespace Orrery.App.Credentials;

/// <summary>Changes the vault's master password. Closes with true once the vault is re-keyed.</summary>
sealed class ChangeMasterPasswordWindow : Window
{
    const int MinimumPasswordLength = 8;

    readonly VaultFileStore _vault;
    readonly TextBox _current = new PasswordBox();
    readonly TextBox _new = new PasswordBox();
    readonly TextBox _confirm = new PasswordBox();
    readonly Notice _error = new(NoticeKind.Error) { IsVisible = false };
    readonly Button _ok;

    public ChangeMasterPasswordWindow(VaultFileStore vault)
    {
        _vault = vault;
        Title = "Change master password";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Dialogs.PanelBrush;

        _ok = new Button { Content = "Change password", IsDefault = true, Padding = new Thickness(16, 6) };
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
                new TextBlock { Text = "Change the vault's master password", FontSize = 18, FontWeight = FontWeight.SemiBold },
                new Notice(NoticeKind.Warning, "Your saved credentials stay as they are. If you forget the new password, there is no way to recover the vault."),
                Dialogs.Field("Current master password", _current),
                Dialogs.Field("New master password", _new),
                Dialogs.Field("Confirm new master password", _confirm),
                _error,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, _ok } },
            },
        };

        Opened += (_, _) => _current.Focus();
    }

    async Task SubmitAsync()
    {
        string current = _current.Text ?? string.Empty, password = _new.Text ?? string.Empty;
        if (current.Length == 0)
        {
            ShowError("Enter the current master password.");
            return;
        }

        if (password.Length < MinimumPasswordLength)
        {
            ShowError($"Use at least {MinimumPasswordLength} characters. A short phrase is easier to remember than a complex word.");
            return;
        }

        if (password != _confirm.Text)
        {
            ShowError("The new passwords don't match.");
            return;
        }

        if (password == current)
        {
            ShowError("The new password is the same as the current one.");
            return;
        }

        _ok.IsEnabled = false;
        _ok.Content = "Changing…";
        try
        {
            await _vault.ChangePasswordAsync(current, password);
            Close(true);
        }
        catch (WrongVaultPasswordException)
        {
            ShowError("The current password is incorrect.");
            _current.Text = string.Empty;
            _current.Focus();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ShowError($"The password wasn't changed. {e.Message}");
        }
        finally
        {
            _ok.IsEnabled = true;
            _ok.Content = "Change password";
        }
    }

    void ShowError(string message)
    {
        _error.Text = message;
        _error.IsVisible = true;
    }
}
