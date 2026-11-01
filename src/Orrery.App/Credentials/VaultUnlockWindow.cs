using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Orrery.App.Controls;
using Orrery.Core.Credentials;

namespace Orrery.App.Credentials;

/// <summary>
/// Creates the vault on first use, or unlocks it. Closes with true once the store is open.
/// </summary>
sealed class VaultUnlockWindow : Window
{
    const int MinimumPasswordLength = 8;

    static readonly IBrush MutedBrush = new SolidColorBrush(Color.Parse("#9AA3B2"));
    static readonly IBrush WarningBrush = new SolidColorBrush(Color.Parse("#F3B672"));
    static readonly IBrush DangerBrush = new SolidColorBrush(Color.Parse("#FF9AA2"));

    readonly ICredentialStore _store;
    readonly bool _creating;
    readonly TextBox _password = new PasswordBox();
    readonly TextBox _confirm = new PasswordBox();
    readonly TextBlock _error = new() { Foreground = DangerBrush, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    readonly Button _ok;

    public VaultUnlockWindow(ICredentialStore store)
    {
        _store = store;
        _creating = !store.Exists;

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

        body.Children.Add(_error);
        body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { cancel, _ok },
        });

        Content = body;
        Opened += (_, _) => _password.Focus();
    }

    static Control Field(string label, Control input)
    {
        Avalonia.Automation.AutomationProperties.SetName(input, label);
        return new StackPanel
        {
            Spacing = 4,
            Children = { new TextBlock { Text = label, FontSize = 12, Foreground = MutedBrush }, input },
        };
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
        !store.IsLocked || await new VaultUnlockWindow(store).ShowDialog<bool>(owner);
}
