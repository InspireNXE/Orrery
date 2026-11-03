using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;

namespace Orrery.App.Controls;

/// <summary>
/// A text box for passwords. Avalonia 11.3 handed a masked text box's real text to UI Automation, so any program
/// reading the screen (not just a screen reader) could read the password, and 12 still does while the password is
/// revealed; this one always reports a dot per character.
/// </summary>
sealed class PasswordBox : TextBox
{
    public const char Mask = '•';

    public PasswordBox()
    {
        PasswordChar = Mask;
    }

    protected override Type StyleKeyOverride => typeof(TextBox);

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    sealed class Peer(PasswordBox owner) : TextBoxAutomationPeer(owner), IValueProvider
    {
        static string Masked(object? text) => new(Mask, (text as string)?.Length ?? 0);

        string? IValueProvider.Value => Masked(Owner.Text);

        bool IValueProvider.IsReadOnly => Owner.IsReadOnly;

        void IValueProvider.SetValue(string? value) => Owner.Text = value;

        protected override void OwnerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == TextBox.TextProperty)
            {
                RaisePropertyChangedEvent(ValuePatternIdentifiers.ValueProperty, Masked(e.OldValue), Masked(e.NewValue));
            }
        }

        protected override string GetLocalizedControlTypeCore() => "password";
    }
}
