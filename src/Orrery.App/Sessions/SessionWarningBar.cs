using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Orrery.App.Controls;

namespace Orrery.App.Sessions;

/// <summary>
/// A warning above a session, pushing it down, that stays until dismissed: what a saved command that types a password did
/// or didn't do, which mustn't go by in a few seconds as the status line's notices do, nor show only while the tab is.
/// A newer one replaces it. Read out when it appears.
/// </summary>
sealed class SessionWarningBar : Border
{
    readonly Notice _notice = new(NoticeKind.Warning);
    readonly Button _dismiss = new() { Content = "Dismiss", VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12, 0, 0, 0) };

    public SessionWarningBar()
    {
        IsVisible = false;
        VerticalAlignment = VerticalAlignment.Top;
        Margin = new Thickness(8, 8, 8, 8);
        Padding = new Thickness(12, 10);
        CornerRadius = new CornerRadius(8);
        BorderThickness = new Thickness(1);
        Bind(BackgroundProperty, this.GetResourceObservable("OrreryRaised"));
        _notice.Message.FontSize = 13;
        AutomationProperties.SetName(_dismiss, "Dismiss this warning");
        _dismiss.Click += (_, _) => IsVisible = false;
        DockPanel.SetDock(_dismiss, Dock.Right);
        Child = new DockPanel { Children = { _dismiss, _notice } };
    }

    public string? Text => IsVisible ? _notice.Text : null;

    public NoticeKind Kind => _notice.Kind;

    public void Show(string text, NoticeKind kind = NoticeKind.Warning)
    {
        _notice.Kind = kind;
        _notice.Text = text;
        BorderBrush = kind == NoticeKind.Error ? Palette.Danger : Palette.Warning;
        IsVisible = true;
        ScreenReader.Announce(this, (kind == NoticeKind.Error ? "Error: " : "Warning: ") + text, "Orrery.Session.Warning", ScreenReader.MostRecent);
    }
}
