using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Orrery.App.Controls;
using Orrery.Core.Sessions;

namespace Orrery.App.Sessions;

/// <summary>
/// The "This session" tab's Connection card: how the open connection is doing, from its <see cref="LinkMonitor"/>.
/// Two figures at the top (screen updates or data in, and the round trip), a minute of each as a graph, the rest as
/// rows, and a plain-words hint when something looks slow. Copy details puts it all on the clipboard for a bug report.
/// </summary>
sealed class ConnectionCard : Border
{
    static readonly IBrush GroupBrush = new SolidColorBrush(Color.Parse("#11151B")).ToImmutable();
    static readonly IBrush TileBrush = new SolidColorBrush(Color.Parse("#181B21")).ToImmutable();
    static readonly IBrush SoftBrush = new SolidColorBrush(Color.Parse("#C3C9D3")).ToImmutable();

    readonly LinkMonitor _monitor;
    readonly string _protocol;
    readonly TextBlock _firstValue = Value(), _firstNote = Note(), _secondValue = Value(), _secondNote = Note();
    readonly TextBlock _firstLabel, _secondLabel;
    readonly Sparkline _firstGraph = new(), _secondGraph = new();
    readonly StackPanel _rows = new() { Spacing = 3 };
    readonly StackPanel _hints = new() { Spacing = 6 };

    public ConnectionCard(LinkMonitor monitor, string protocol)
    {
        _monitor = monitor;
        _protocol = protocol;
        bool desktop = monitor.CountsUpdates;
        _firstLabel = Label(desktop ? "Screen updates" : "Data in");
        _secondLabel = Label("Round trip");
        Name = "ConnectionCard";

        var heading = new TextBlock { Text = "Connection", FontSize = 13, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetHeadingLevel(heading, 2);
        var copy = new Button { Content = "Copy details", FontSize = 11, Padding = new Thickness(8, 2), Name = "CopyConnectionDetails" };
        ToolTip.SetTip(copy, "Copies these numbers, for a bug report");
        copy.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(Details());
            }
        };

        Background = GroupBrush;
        BorderBrush = Pane.LineBrush;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(10);
        Padding = new Thickness(14, 12);
        Margin = new Thickness(0, 10, 0, 0);
        Child = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new DockPanel { Children = { Pane.Docked(copy, Dock.Right), heading } },
                new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,8,*"),
                    Children = { Tile(_firstLabel, _firstValue, _firstNote, _firstGraph, 0), Tile(_secondLabel, _secondValue, _secondNote, _secondGraph, 2) },
                },
                _rows,
                _hints,
            },
        };

        Show();
        AttachedToVisualTree += (_, _) =>
        {
            _monitor.Changed += OnChanged;
            Show();
        };
        DetachedFromVisualTree += (_, _) => _monitor.Changed -= OnChanged;
    }

    void OnChanged(object? sender, EventArgs e) => Show();

    static Border Tile(TextBlock label, TextBlock value, TextBlock note, Sparkline graph, int column)
    {
        var tile = new Border
        {
            Background = TileBrush,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8),
            Child = new StackPanel { Spacing = 2, Children = { label, value, graph, note } },
        };
        Grid.SetColumn(tile, column);
        return tile;
    }

    static TextBlock Label(string text) => new() { Text = text, FontSize = 11, Foreground = SoftBrush };

    static TextBlock Value() => new() { FontSize = 18, FontWeight = FontWeight.SemiBold };

    static TextBlock Note() => new() { FontSize = 11, Foreground = Dialogs.MutedBrush, TextTrimming = TextTrimming.CharacterEllipsis };

    void Show()
    {
        var latest = _monitor.Latest;
        var history = _monitor.History;
        bool desktop = _monitor.CountsUpdates;

        if (desktop)
        {
            _firstValue.Text = latest is { } s ? $"{s.UpdatesPerSecond:0}/s" : "–";
            _firstNote.Text = latest is { } busy && _monitor.Link is not null ? $"decoding {busy.BusyShare:P0} of the time" : "a still screen sends none";
            _firstGraph.Values = history.Select(h => h.UpdatesPerSecond).ToList();
        }
        else
        {
            _firstValue.Text = latest is { } s ? LinkMonitor.Rate(s.ReceivedPerSecond) : "–";
            _firstNote.Text = latest is { } sent ? $"out {LinkMonitor.Rate(sent.SentPerSecond)}" : string.Empty;
            _firstGraph.Values = history.Select(h => h.ReceivedPerSecond).ToList();
        }

        var rtt = latest?.RoundTrip;
        _secondValue.Text = rtt is { } r ? LinkMonitor.Milliseconds(r) : "–";
        _secondValue.Foreground = rtt >= LinkMonitor.SlowRoundTrip ? Palette.Warning : null;
        _secondNote.Text = rtt is not null ? "as the system measures it" : RoundTripMissing();
        _secondGraph.Values = history.Select(h => h.RoundTrip?.TotalMilliseconds ?? 0).ToList();
        _secondGraph.IsVisible = history.Any(h => h.RoundTrip is not null);
        ToolTip.SetTip(_secondNote, rtt is null ? RoundTripWhy() : null);

        _rows.Children.Clear();
        foreach (var (label, value) in Rows())
        {
            _rows.Children.Add(new DockPanel
            {
                Children =
                {
                    Pane.Docked(new TextBlock { Text = value, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 200 }, Dock.Right),
                    new TextBlock { Text = label, FontSize = 12, Foreground = SoftBrush },
                },
            });
        }

        _hints.Children.Clear();
        foreach (var hint in _monitor.Hints())
        {
            _hints.Children.Add(new Border
            {
                Background = new SolidColorBrush(Palette.Warning.Color, 0.14),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8),
                Child = new TextBlock { Text = hint, FontSize = 12, Foreground = Palette.Warning, TextWrapping = TextWrapping.Wrap },
            });
        }
    }

    string RoundTripMissing() => _monitor.Link is null ? "not measured" : "not measured here";

    string RoundTripWhy() => _monitor.Link is null
        ? "This kind of session doesn't measure it."
        : "Through an SSH tunnel or jump host it would only measure this computer, so it's left out. It also needs a system that reports it.";

    /// <summary>The rows under the figures, also what Copy details copies.</summary>
    IEnumerable<(string Label, string Value)> Rows()
    {
        var session = _monitor.Session;
        if (_monitor.ConnectedAt is { } at)
        {
            yield return ("Connected for", LinkMonitor.Duration(DateTimeOffset.Now - at));
        }

        if (_monitor.Link?.ConnectTime is { } connect)
        {
            yield return ("Connecting took", LinkMonitor.Milliseconds(connect));
        }

        if (session is IFrameBufferSession frames && frames.Width > 0)
        {
            yield return ("Desktop", $"{frames.Width} × {frames.Height}");
        }

        if (_monitor.Link?.LastResize is { } resize)
        {
            yield return ("Last resize", LinkMonitor.Milliseconds(resize));
        }

        if (_monitor.Link is not null)
        {
            var totals = _monitor.Totals;
            if (_monitor.CountsUpdates && _monitor.Latest is { } latest)
            {
                yield return ("Data now", $"{LinkMonitor.Rate(latest.ReceivedPerSecond)} in, {LinkMonitor.Rate(latest.SentPerSecond)} out");
            }

            yield return ("This session", $"{LinkMonitor.Size(totals.Received)} in, {LinkMonitor.Size(totals.Sent)} out");
        }
    }

    /// <summary>Everything on the card as text.</summary>
    internal string Details()
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"{_protocol} to {_monitor.Session.Target.Host}:{_monitor.Session.Target.Port}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Orrery {typeof(ConnectionCard).Assembly.GetName().Version}");
        text.AppendLine(CultureInfo.InvariantCulture, $"{_firstLabel.Text}: {_firstValue.Text} ({_firstNote.Text})");
        text.AppendLine(CultureInfo.InvariantCulture, $"Round trip: {_secondValue.Text} ({_secondNote.Text})");
        foreach (var (label, value) in Rows())
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"{label}: {value}");
        }

        foreach (var hint in _monitor.Hints())
        {
            text.AppendLine(hint);
        }

        return text.ToString();
    }
}

/// <summary>A minute of a figure as a line, scaled to its own highest value, newest on the right.</summary>
sealed class Sparkline : Control
{
    IReadOnlyList<double> _values = [];

    public Sparkline()
    {
        Height = 28;
        Margin = new Thickness(0, 4, 0, 2);
    }

    public IReadOnlyList<double> Values
    {
        get => _values;
        set
        {
            _values = value;
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        context.DrawLine(new Pen(Pane.LineBrush, 1), new Point(0, size.Height - 0.5), new Point(size.Width, size.Height - 0.5));
        if (_values.Count < 2 || size.Width <= 0)
        {
            return;
        }

        double max = Math.Max(_values.Max(), 1e-9);
        double step = size.Width / (LinkMonitor.HistoryLength - 1);
        double left = size.Width - step * (_values.Count - 1);
        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            for (int i = 0; i < _values.Count; i++)
            {
                var point = new Point(left + i * step, size.Height - 1 - (size.Height - 3) * Math.Clamp(_values[i] / max, 0, 1));
                if (i == 0)
                {
                    g.BeginFigure(point, false);
                }
                else
                {
                    g.LineTo(point);
                }
            }

            g.EndFigure(false);
        }

        context.DrawGeometry(null, new Pen(Palette.Accent, 1.5, lineJoin: PenLineJoin.Round), line);
    }
}
