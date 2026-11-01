using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Orrery.App.Controls;
using Orrery.Core.Connections;

namespace Orrery.App.Connections;

/// <summary>
/// The side panel listing recently opened connections, newest first. Double-click or Enter opens one
/// again; the context menu removes it from the list. With remembering turned off it stays, empty, and says so,
/// rather than leaving the rail with a button that has gone.
/// </summary>
sealed class RecentPanel : DockPanel
{
    readonly RecentConnections _recent;
    readonly ListBox _list = new() { Background = Brushes.Transparent, Padding = new Thickness(12, 0), Classes = { "side" } };
    const string EmptyNote = "Connections you open show up here.";
    const string OffNote = "Recent connections aren't remembered. Turn on Remember recent connections in Settings, General, to keep a list here.";

    readonly TextBlock _empty = Pane.EmptyText(EmptyNote, side: true);
    readonly Button _clear;

    public RecentPanel(RecentConnections recent)
    {
        _recent = recent;

        _list.ItemTemplate = new FuncDataTemplate<RecentConnection>((r, _) => r is null ? new Panel() : Row(r));
        _list.DoubleTapped += (_, _) => OpenSelected();
        _list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                OpenSelected();
            }
        };

        var open = new MenuItem { Header = "Connect" };
        open.Click += (_, _) => OpenSelected();
        var remove = new MenuItem { Header = "Remove from recent" };
        remove.Click += (_, _) =>
        {
            if (_list.SelectedItem is RecentConnection selected)
            {
                _recent.Remove(selected);
            }
        };
        _list.ContextMenu = new ContextMenu { Items = { open, remove } };
        _list.ContextMenu.Opening += (_, e) => e.Cancel = _list.SelectedItem is null;

        _clear = new Button { Content = "Clear", MinHeight = 0, Height = 24, Padding = new Thickness(8, 0), FontSize = 11 };
        _clear.Classes.Add("icon");
        ToolTip.SetTip(_clear, "Clear the recent list");
        _clear.Click += (_, _) => _recent.Clear();

        Children.Add(Pane.Docked(new DockPanel
        {
            Margin = new Thickness(14, 10, 10, 6),
            Height = 28,
            Children =
            {
                Pane.Docked(_clear, Dock.Right),
                new TextBlock { Text = "RECENT", Classes = { "section" }, VerticalAlignment = VerticalAlignment.Center },
            },
        }, Dock.Top));
        Children.Add(new Panel { Children = { _list, _empty } });

        _recent.Changed += (_, _) => Refresh();
        Refresh();
    }

    /// <summary>Raised when a row is opened.</summary>
    public event EventHandler<RecentConnection>? OpenRequested;

    /// <summary>Re-renders the rows, so the "5 min ago" times stay current.</summary>
    public void Refresh()
    {
        var items = _recent.Items;
        _list.ItemsSource = items.ToList();
        _list.IsVisible = _clear.IsVisible = items.Count > 0;
        _empty.IsVisible = items.Count == 0;
        _empty.Text = _recent.Enabled ? EmptyNote : OffNote;
    }

    void OpenSelected()
    {
        if (_list.SelectedItem is RecentConnection selected)
        {
            OpenRequested?.Invoke(this, selected);
        }
    }

    static Control Row(RecentConnection r)
    {
        var user = string.IsNullOrEmpty(r.Username) ? string.Empty : $"{r.Username}@";
        var port = r.Port == ProtocolInfo.DefaultPort(r.Protocol) ? string.Empty : $":{r.Port}";

        // A web page's host can carry a path, so it's shown as the address the browser opened.
        var address = ProtocolInfo.IsWeb(r.Protocol) && WebAddress.Url(r.Protocol, r.Host, r.Port) is { } url ? url.AbsoluteUri : $"{user}{r.Host}{port}";
        return new DockPanel
        {
            Margin = new Thickness(0, 4),
            Children =
            {
                Pane.Docked(new Border
                {
                    Background = ProtocolStyle.Background(r.Protocol),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(5, 1),
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = ProtocolInfo.Label(r.Protocol), FontSize = 10, FontWeight = FontWeight.Medium, FontFamily = Dialogs.MonoFont, Foreground = ProtocolStyle.Foreground(r.Protocol) },
                }, Dock.Right),
                new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = r.Title, TextTrimming = TextTrimming.CharacterEllipsis },
                        new TextBlock
                        {
                            Text = $"{address} · {Ago(r.OpenedAt)}",
                            FontSize = 11,
                            Foreground = Dialogs.MutedBrush,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                        },
                    },
                },
            },
        };
    }

    static string Ago(DateTimeOffset when)
    {
        var age = DateTimeOffset.Now - when;
        return age switch
        {
            { TotalMinutes: < 1 } => "just now",
            { TotalHours: < 1 } => $"{(int)age.TotalMinutes} min ago",
            { TotalDays: < 1 } => $"{(int)age.TotalHours} h ago",
            _ when when.LocalDateTime.Date == DateTime.Today.AddDays(-1) => "yesterday",
            _ => when.LocalDateTime.ToString("d MMM"),
        };
    }
}
