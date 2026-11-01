using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;

namespace Orrery.App.Controls;

/// <summary>
/// A multi-line text box for a pasted SSH private key. A key is a secret, so it's masked until
/// <see cref="Revealed"/> is set, and it never reaches UI Automation, which any program reading the screen can
/// use: the peer reports only "Private key, N lines", whether or not the text is showing. Pasting works as in
/// any text box; copying and cutting are off while it's masked.
/// </summary>
sealed class PrivateKeyBox : TextBox
{
    public PrivateKeyBox()
    {
        AcceptsReturn = true;
        FontFamily = Dialogs.MonoFont;
        FontSize = 11;
        Show(revealed: false);
    }

    protected override Type StyleKeyOverride => typeof(TextBox);

    /// <summary>Whether the key is shown on screen. Off by default; this has no effect on what automation is told.</summary>
    public bool Revealed
    {
        get => PasswordChar == default;
        set => Show(value);
    }

    void Show(bool revealed)
    {
        PasswordChar = revealed ? default : PasswordBox.Mask;

        // Masked, the dots wrap like text, so the box looks like a block of key rather than one long line.
        TextWrapping = revealed ? Avalonia.Media.TextWrapping.NoWrap : Avalonia.Media.TextWrapping.Wrap;
    }

    /// <summary>What automation is told instead of the key: how many lines it has, never any of its text.</summary>
    internal static string Describe(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "Private key, empty";
        }

        int lines = 1;
        foreach (var c in text.AsSpan().TrimEnd("\r\n"))
        {
            if (c == '\n')
            {
                lines++;
            }
        }

        return $"Private key, {lines} line{(lines == 1 ? "" : "s")}";
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    sealed class Peer(PrivateKeyBox owner) : TextBoxAutomationPeer(owner), IValueProvider
    {
        string? IValueProvider.Value => Describe(Owner.Text);

        bool IValueProvider.IsReadOnly => Owner.IsReadOnly;

        void IValueProvider.SetValue(string? value) => Owner.Text = value;

        protected override void OwnerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == TextBox.TextProperty)
            {
                RaisePropertyChangedEvent(ValuePatternIdentifiers.ValueProperty, Describe(e.OldValue as string), Describe(e.NewValue as string));
            }
        }

        protected override string GetLocalizedControlTypeCore() => "private key";
    }
}
