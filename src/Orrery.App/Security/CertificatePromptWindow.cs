using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Orrery.App.Controls;
using Orrery.Core.Security;

namespace Orrery.App.Security;

/// <summary>
/// Asks the user whether to trust a server certificate. Closing the window counts as Cancel.
/// For a changed certificate, Cancel is the default button.
/// </summary>
sealed class CertificatePromptWindow : Window
{

    public CertificatePromptWindow(CertificatePrompt prompt)
    {
        bool changed = prompt.Kind == CertificatePromptKind.Changed;

        Title = changed ? "Certificate changed" : "Verify server";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Dialogs.PanelBrush;

        var body = new StackPanel { Spacing = 14, Margin = new Thickness(24) };

        body.Children.Add(Dialogs.TrustHeading(
            changed ? $"The certificate for {prompt.Host} has changed" : $"Verify the identity of {prompt.Host}",
            changed));

        body.Children.Add(new TextBlock
        {
            Text = changed
                ? "This server presented a different certificate from the one you trusted before. That's expected after "
                  + "the server is rebuilt or its certificate is renewed, but it can also mean someone is intercepting "
                  + "the connection. Don't continue unless you know why it changed."
                : "Orrery can't confirm this is the server you meant to reach. Check the fingerprint with whoever "
                  + "manages the server before you trust it. Your credentials haven't been sent yet.",
            TextWrapping = TextWrapping.Wrap,
        });

        foreach (var problem in prompt.Problems)
        {
            body.Children.Add(new Notice(NoticeKind.Warning, problem));
        }

        var details = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowSpacing = 6, ColumnSpacing = 16 };
        AddRow(details, "Host", $"{prompt.Host}:{prompt.Port}");
        AddRow(details, "Subject", prompt.Subject);
        AddRow(details, "Issuer", prompt.Issuer);
        AddRow(details, "Valid", $"{prompt.NotBefore:yyyy-MM-dd} to {prompt.NotAfter:yyyy-MM-dd}");
        AddRow(details, "SHA-256", FormatFingerprint(prompt.Sha256), mono: true);
        if (prompt.PinnedSha256 is { } pinned)
        {
            AddRow(details, "Trusted before", FormatFingerprint(pinned), mono: true);
        }

        body.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.Parse("#1B1E24")),
            BorderBrush = new SolidColorBrush(Color.Parse("#2A2F38")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Child = details,
        });

        var cancel = MakeButton("Cancel", CertificateTrustDecision.Reject);
        var once = MakeButton("Connect once", CertificateTrustDecision.TrustOnce);
        var always = MakeButton(changed ? "Trust new certificate" : "Trust and connect", CertificateTrustDecision.TrustAlways);

        var primary = changed ? cancel : always;
        primary.Classes.Add("accent");
        primary.IsDefault = true;
        cancel.IsCancel = true;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 6, 0, 0),
        };
        buttons.Children.AddRange(changed ? [always, once, cancel] : [cancel, once, always]);
        body.Children.Add(buttons);

        Content = body;
    }

    Button MakeButton(string text, CertificateTrustDecision decision)
    {
        var button = new Button { Content = text, Padding = new Thickness(14, 6) };
        button.Click += (_, _) => Close(decision);
        return button;
    }

    static void AddRow(Grid grid, string label, string value, bool mono = false)
    {
        int row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        var labelBlock = new TextBlock { Text = label, Foreground = Dialogs.MutedBrush, FontSize = 12 };
        var valueBlock = new SelectableTextBlock
        {
            Text = value,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            FontFamily = mono ? Dialogs.MonoFont : FontFamily.Default,
        };

        Grid.SetRow(labelBlock, row);
        Grid.SetRow(valueBlock, row);
        Grid.SetColumn(valueBlock, 1);
        grid.Children.Add(labelBlock);
        grid.Children.Add(valueBlock);
    }

    /// <summary>"AB12CD…" → "AB:12:CD:…", split over two lines so it is easy to compare by eye.</summary>
    static string FormatFingerprint(string hex)
    {
        var pairs = Enumerable.Range(0, hex.Length / 2).Select(i => hex.Substring(i * 2, 2)).ToArray();
        int half = (pairs.Length + 1) / 2;
        return string.Join(':', pairs[..half]) + "\n" + string.Join(':', pairs[half..]);
    }
}
