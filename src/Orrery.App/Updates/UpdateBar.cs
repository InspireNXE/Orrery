using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Orrery.App.Controls;

namespace Orrery.App.Updates;

/// <summary>
/// The slim bar above the session tabs saying a newer Orrery is out (or downloaded and ready), drawn like the offer to
/// reopen last time's sessions. It never covers a session. "See what's new" opens the details, Later puts it away until
/// the next start, and × skips the version.
/// </summary>
sealed class UpdateBar : Border
{
    static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#C3C9D3")).ToImmutable();
    static readonly IBrush AccentBrush = new SolidColorBrush(Color.Parse("#5B9DFF")).ToImmutable();

    readonly UpdateController _updates;

    public UpdateBar(UpdateController updates)
    {
        _updates = updates;
        Padding = new Thickness(14, 4, 8, 4);
        Background = Dialogs.PanelBrush;
        BorderBrush = Pane.LineBrush;
        BorderThickness = new Thickness(0, 0, 0, 1);
        IsVisible = false;

        Text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = TextBrush, TextTrimming = TextTrimming.CharacterEllipsis };
        Details = new Button { Content = "See what's new", MinHeight = 26, Margin = new Thickness(0, 0, 8, 0) };
        Details.Classes.Add("accent");
        LaterButton = new Button { Content = "Later", MinHeight = 26 };
        SkipButton = new Button { Content = StrokeIcon.Create(Icons.Close, 14), Width = 26, Height = 26, Margin = new Thickness(4, 0, 0, 0) };
        SkipButton.Classes.Add("icon");
        ToolTip.SetTip(SkipButton, "Skip this version");
        AutomationProperties.SetName(SkipButton, "Skip this version");

        Details.Click += async (_, _) => await _updates.ShowDetailsAsync();
        LaterButton.Click += (_, _) => _updates.Later();
        SkipButton.Click += (_, _) => _updates.Skip();

        var icon = StrokeIcon.Create(Icons.Download, 15, AccentBrush);
        icon.Margin = new Thickness(0, 0, 10, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;
        Child = new DockPanel
        {
            Children =
            {
                Pane.Docked(SkipButton, Dock.Right),
                Pane.Docked(LaterButton, Dock.Right),
                Pane.Docked(Details, Dock.Right),
                Pane.Docked(icon, Dock.Left),
                Text,
            },
        };

        _updates.Changed += (_, _) => Refresh();
        Refresh();
    }

    internal TextBlock Text { get; }

    internal Button Details { get; }

    internal Button LaterButton { get; }

    internal Button SkipButton { get; }

    void Refresh()
    {
        bool wasShown = IsVisible;
        Text.Text = _updates.BarText;
        IsVisible = _updates.BarShown;
        AutomationProperties.SetName(this, Text.Text);
        if (IsVisible && !wasShown)
        {
            ScreenReader.Announce(this, Text.Text, "Orrery.Update", ScreenReader.MostRecent);
        }
    }
}
