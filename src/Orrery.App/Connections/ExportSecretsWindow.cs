using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Orrery.App.Controls;
using Orrery.Core.Credentials;
using Orrery.Core.Export;

namespace Orrery.App.Connections;

/// <summary>The export's password when it includes saved passwords and keys; null leaves them out.</summary>
sealed record ExportSecretsChoice(string? Password);

/// <summary>
/// Asked before an Orrery or mRemoteNG export that uses saved credentials: whether to include their
/// passwords (and, for Orrery, keys), and the password that protects them. Closes with null when canceled.
/// </summary>
sealed class ExportSecretsWindow : Window
{
    readonly CheckBox _include;
    readonly TextBox _password = new PasswordBox { IsEnabled = false };
    readonly TextBox _confirm = new PasswordBox { IsEnabled = false };
    readonly PasswordMeter _meter;
    readonly Notice _error = new(NoticeKind.Error) { IsVisible = false };

    public ExportSecretsWindow(ExportFormat format)
    {
        bool orrery = format == ExportFormat.Orrery;
        Title = "Export";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Dialogs.PanelBrush;

        _include = new CheckBox { Content = orrery ? "Include saved passwords and keys" : "Include saved passwords" };
        _include.IsCheckedChanged += (_, _) =>
        {
            bool on = _include.IsChecked == true;
            _password.IsEnabled = _confirm.IsEnabled = on;
            _error.IsVisible = false;
            if (on)
            {
                _password.Focus();
            }
        };

        var ok = new Button { Content = "Export", IsDefault = true, Padding = new Thickness(16, 6) };
        ok.Classes.Add("accent");
        ok.Click += (_, _) => Submit();
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(16, 6) };
        cancel.Click += (_, _) => Close(null);

        Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = "Include saved passwords?", FontSize = 18, FontWeight = FontWeight.SemiBold },
                new TextBlock
                {
                    Text = orrery
                        ? "Some of these connections use saved credentials. The export can include their passwords and keys. "
                            + "The whole file is then encrypted with a password you choose, and Orrery asks for it when the file is imported."
                        : "Some of these connections use saved credentials. The export can include their passwords, protected by a master "
                            + "password you choose, which mRemoteNG asks for when it opens the file. mRemoteNG has no place for SSH keys, so those stay out. "
                            + "mRemoteNG's protection is weaker against guessing than an Orrery export's, so use a long password, or export as Orrery if it's going to Orrery.",
                    TextWrapping = TextWrapping.Wrap,
                },
                _include,
                Dialogs.Field("Password for this file", _password),
                (_meter = new PasswordMeter(_password, "export")),
                Dialogs.Field("Confirm password", _confirm),
                new Notice(NoticeKind.Warning, "Anyone with the file and this password can read every password in it, and there's no way to recover them if it's forgotten."),
                _error,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, ok } },
            },
        };

        Opened += (_, _) => _include.Focus();
    }

    /// <summary>
    /// Why the passwords can't be used, or null when they can (or aren't wanted). The file's password follows the same
    /// rules as the vault's: it guards the same secrets, and the file can be copied anywhere.
    /// </summary>
    internal static string? Problem(bool include, string password, string confirm, IEnumerable<string>? personal = null) =>
        !include ? null
        : PasswordRules.Check(password, personal).Problem is { } problem ? problem
        : password != confirm ? "The passwords don't match."
        : null;

    void Submit()
    {
        bool include = _include.IsChecked == true;
        string password = _password.Text ?? string.Empty;
        // The meter knows the words close to the user as well.
        if (((include ? _meter.Check().Problem : null) ?? Problem(include, password, _confirm.Text ?? string.Empty)) is { } problem)
        {
            _error.Text = problem;
            _error.IsVisible = true;
            return;
        }

        Close(new ExportSecretsChoice(include ? password : null));
    }
}
