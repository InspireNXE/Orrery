using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Orrery.Core.Security;

namespace Orrery.App.Security;

/// <summary>
/// Asks whether to trust an SSH server's host key. Closing counts as Cancel; for a changed key, Cancel is
/// the default button. The prompt notes that nothing has been sent yet.
/// </summary>
sealed class HostKeyPromptWindow : Window
{
    public HostKeyPromptWindow(HostKeyPrompt prompt)
    {
        bool changed = prompt.Kind == CertificatePromptKind.Changed;

        Title = changed ? "Host key changed" : "Verify host";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Dialogs.PanelBrush;

        var cancel = Button("Cancel", CertificateTrustDecision.Reject);
        var once = Button("Connect once", CertificateTrustDecision.TrustOnce);
        var always = Button(changed ? "Trust new key" : "Trust and connect", CertificateTrustDecision.TrustAlways);
        var primary = changed ? cancel : always;
        primary.Classes.Add("accent");
        primary.IsDefault = true;
        cancel.IsCancel = true;

        var details = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowSpacing = 6, ColumnSpacing = 16 };
        AddRow(details, "Host", $"{prompt.Host}:{prompt.Port}");
        AddRow(details, "Key type", prompt.KeyType);
        AddRow(details, "SHA-256", prompt.Sha256Fingerprint);
        if (prompt.PinnedFingerprint is { } pinned)
        {
            AddRow(details, "Trusted before", pinned);
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.AddRange(changed ? [always, once, cancel] : [cancel, once, always]);

        Content = new StackPanel
        {
            Spacing = 14,
            Margin = new Thickness(24),
            Children =
            {
                new TextBlock
                {
                    Text = changed ? $"The host key for {prompt.Host} has changed" : $"Verify the identity of {prompt.Host}",
                    FontSize = 18,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = changed ? Dialogs.DangerBrush : null,
                    TextWrapping = TextWrapping.Wrap,
                },
                new TextBlock
                {
                    Text = changed
                        ? "This server presented a different key from the one you trusted before. That's expected after the server "
                          + "is reinstalled, but it can also mean someone is intercepting the connection. Don't continue unless you "
                          + "know why it changed."
                        : "This is the first connection to this server. Check the fingerprint with whoever manages it (on the server: "
                          + "ssh-keygen -lf /etc/ssh/ssh_host_*_key.pub). Your password hasn't been sent yet.",
                    TextWrapping = TextWrapping.Wrap,
                },
                new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#1B1E24")),
                    BorderBrush = new SolidColorBrush(Color.Parse("#2A2F38")),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12),
                    Child = details,
                },
                buttons,
            },
        };
    }

    Button Button(string text, CertificateTrustDecision decision)
    {
        var button = new Button { Content = text, Padding = new Thickness(14, 6) };
        button.Click += (_, _) => Close(decision);
        return button;
    }

    static void AddRow(Grid grid, string label, string value)
    {
        int row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        var labelBlock = Dialogs.Label(label);
        var valueBlock = new SelectableTextBlock { Text = value, TextWrapping = TextWrapping.Wrap, FontSize = 12, FontFamily = Dialogs.MonoFont };
        Grid.SetRow(labelBlock, row);
        Grid.SetRow(valueBlock, row);
        Grid.SetColumn(valueBlock, 1);
        grid.Children.Add(labelBlock);
        grid.Children.Add(valueBlock);
    }
}
