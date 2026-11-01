using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Orrery.App.Controls;

namespace Orrery.App.Connections;

/// <summary>A key file's passphrase, and whether to save it as a credential for the next time.</summary>
sealed record KeyPassphraseResult(string Passphrase, bool Save);

/// <summary>
/// Asks for the passphrase of an encrypted SSH key file. It names the file (never the folder it's in) and the
/// connection that wants it. Closes with null when cancelled.
/// </summary>
sealed class KeyPassphraseWindow : Window
{
    readonly TextBox _passphrase = new PasswordBox();
    readonly CheckBox _save = new() { Content = "Save the passphrase as a credential" };

    /// <param name="keyName">The key file's name.</param>
    /// <param name="wrong">The last passphrase didn't open the key.</param>
    /// <param name="offerSave">False when there's nowhere to save it.</param>
    public KeyPassphraseWindow(string title, string keyName, bool wrong, bool offerSave)
    {
        Title = "Key passphrase";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Dialogs.PanelBrush;

        var ok = new Button { Content = "Continue", IsDefault = true, Padding = new Thickness(16, 6) };
        ok.Classes.Add("accent");
        ok.Click += (_, _) => Close(new KeyPassphraseResult(_passphrase.Text ?? string.Empty, offerSave && _save.IsChecked == true));
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(16, 6) };
        cancel.Click += (_, _) => Close(null);

        _save.IsVisible = offerSave;
        var body = new StackPanel { Spacing = 12, Margin = new Thickness(24) };
        body.Children.Add(Dialogs.Heading(new TextBlock { Text = "Enter the key's passphrase", FontSize = 16, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap }));
        body.Children.Add(new TextBlock { Text = title, Foreground = Dialogs.MutedBrush, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
        body.Children.Add(new TextBlock { Text = $"The key file {keyName} is protected. Orrery asks every time unless you save the passphrase.", TextWrapping = TextWrapping.Wrap });
        if (wrong)
        {
            body.Children.Add(new Notice(NoticeKind.Error, "That passphrase didn't open the key. Try again."));
        }

        body.Children.Add(Dialogs.Field("Passphrase", _passphrase));
        body.Children.Add(_save);
        body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, ok } });
        Content = body;
        Opened += (_, _) => _passphrase.Focus();
    }
}
