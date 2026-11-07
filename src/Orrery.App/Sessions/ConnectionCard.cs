using System.Globalization;
using System.Reflection;
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
/// A verdict first (Good or Slow, with the round trip and screen updates beside it), then five minutes of both as one
/// graph with what happened marked on it, the details in two short groups (Now, This session), where connecting spent
/// its time, and the last few events. Copy details puts it all on the clipboard for a bug report.
/// </summary>
sealed class ConnectionCard : Border
{
    static readonly IBrush GroupBrush = new SolidColorBrush(Color.Parse("#11151B")).ToImmutable();
    static readonly IBrush SoftBrush = new SolidColorBrush(Color.Parse("#C3C9D3")).ToImmutable();
    static readonly IBrush GrayBrush = new SolidColorBrush(Color.Parse("#888780")).ToImmutable();
    static readonly IBrush DarkGrayBrush = new SolidColorBrush(Color.Parse("#5F5E5A")).ToImmutable();
    static readonly IBrush VioletBrush = new SolidColorBrush(Color.Parse("#7F77DD")).ToImmutable();

    /// <summary>How many events the Recent list shows.</summary>
    const int RecentShown = 5;

    readonly LinkMonitor _monitor;
    readonly string _protocol;
    readonly Border _dot = new() { Width = 9, Height = 9, CornerRadius = new CornerRadius(4.5), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
    readonly TextBlock _verdict = new() { FontSize = 15, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Name = "ConnectionVerdict" };
    readonly TextBlock _figures = new() { FontSize = 12, Foreground = SoftBrush, VerticalAlignment = VerticalAlignment.Center };
    readonly LinkGraph _graph = new();
    readonly TextBlock _primaryLegend, _secondaryLegend = new() { Text = "Round trip", FontSize = 11, Foreground = Dialogs.MutedBrush };
    readonly Control _secondaryKey;
    readonly StackPanel _body = new() { Spacing = 3 };
    readonly StackPanel _hints = new() { Spacing = 6 };

    public ConnectionCard(LinkMonitor monitor, string protocol)
    {
        _monitor = monitor;
        _protocol = protocol;
        Name = "ConnectionCard";
        _primaryLegend = new TextBlock { Text = monitor.CountsUpdates ? "Screen updates" : "Data in", FontSize = 11, Foreground = Dialogs.MutedBrush };
        _secondaryKey = Key(_secondaryLegend, GrayBrush, dashed: true);

        var heading = new TextBlock { Text = "Connection", FontSize = 13, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetHeadingLevel(heading, 2);
        var copy = new Button { Content = "Copy details", FontSize = 11, Padding = new Thickness(8, 2), Name = "CopyConnectionDetails" };
        ToolTip.SetTip(copy, "Copies everything on this card, for a bug report");
        copy.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(Details());
            }
        };

        var legend = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        var keys = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, Children = { Key(_primaryLegend, Palette.Accent, dashed: false), _secondaryKey } };
        var right = Muted("now");
        Grid.SetColumn(keys, 1);
        Grid.SetColumn(right, 2);
        legend.Children.Add(Muted(LinkMonitor.Duration(TimeSpan.FromSeconds(LinkMonitor.HistoryLength)) + " ago"));
        legend.Children.Add(keys);
        legend.Children.Add(right);

        Background = GroupBrush;
        BorderBrush = Pane.LineBrush;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(10);
        Padding = new Thickness(14, 12);
        Margin = new Thickness(0, 10, 0, 0);
        Child = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new DockPanel { Children = { Pane.Docked(copy, Dock.Right), heading } },
                new DockPanel { Children = { Pane.Docked(_figures, Dock.Right), _dot, _verdict } },
                _hints,
                new StackPanel { Spacing = 2, Children = { _graph, legend } },
                _body,
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

    static TextBlock Muted(string text) => new() { Text = text, FontSize = 11, Foreground = Dialogs.MutedBrush };

    /// <summary>A legend entry: a short line in the series' color, then its name.</summary>
    static StackPanel Key(TextBlock label, IBrush brush, bool dashed)
    {
        var line = new Border { Width = 12, Height = dashed ? 1 : 2, Background = brush, VerticalAlignment = VerticalAlignment.Center };
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { line, label } };
    }

    void Show()
    {
        var latest = _monitor.Latest;
        var history = _monitor.History;
        bool desktop = _monitor.CountsUpdates;

        _verdict.Text = _monitor.Verdict();
        _dot.Background = _monitor.Health switch
        {
            LinkHealth.Good => Palette.StateUp,
            LinkHealth.Slow => Palette.Warning,
            _ => GrayBrush,
        };
        _figures.Text = Figures();
        ToolTip.SetTip(_figures, latest?.RoundTrip is not null ? "The round trip as the system measures it" : null);

        bool hasRoundTrip = history.Any(h => h.RoundTrip is not null);
        _graph.Primary = history.Select(h => desktop ? h.UpdatesPerSecond : h.ReceivedPerSecond).ToList();
        _graph.Secondary = hasRoundTrip ? history.Select(h => h.RoundTrip?.TotalMilliseconds ?? 0).ToList() : [];
        _graph.Marks = Marks();
        _secondaryKey.IsVisible = hasRoundTrip;

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

        _body.Children.Clear();
        Group("Now", NowRows());
        Group("This session", SessionRows());

        var steps = _monitor.ConnectSteps();
        if (steps.Count > 0 && _monitor.Link?.ConnectTime is { } total)
        {
            _body.Children.Add(Heading($"Connecting took {LinkMonitor.Milliseconds(total)}"));
            _body.Children.Add(StepsBar(steps));
            _body.Children.Add(StepsLegend(steps));
        }

        if (_monitor.Events.Count > 0)
        {
            Group("Recent", Recent().Select(e => (e.Label, e.Value, (string?)null, e.Brush)));
        }
    }

    /// <summary>The two figures beside the verdict: the round trip, then screen updates or data in.</summary>
    string Figures()
    {
        if (_monitor.Latest is not { } latest)
        {
            return "–";
        }

        string rate = _monitor.CountsUpdates ? $"{latest.UpdatesPerSecond:0}/s" : $"{LinkMonitor.Rate(latest.ReceivedPerSecond)} in";
        return latest.RoundTrip is { } rtt ? $"{LinkMonitor.Milliseconds(rtt)} · {rate}" : rate;
    }

    static TextBlock Heading(string text) => new() { Text = text, FontSize = 11, Foreground = Dialogs.MutedBrush, Margin = new Thickness(0, 6, 0, 1) };

    void Group(string heading, IEnumerable<(string Label, string Value, string? Tip, IBrush? Mark)> rows)
    {
        var list = rows.ToList();
        if (list.Count == 0)
        {
            return;
        }

        _body.Children.Add(Heading(heading));
        foreach (var (label, value, tip, mark) in list)
        {
            var valueText = new TextBlock { Text = value, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 200 };
            ToolTip.SetTip(valueText, tip);
            var labelText = new TextBlock { Text = label, FontSize = 12, Foreground = SoftBrush, TextTrimming = TextTrimming.CharacterEllipsis };
            var row = new DockPanel { Children = { Pane.Docked(valueText, Dock.Right) } };
            if (mark is not null)
            {
                row.Children.Add(Pane.Docked(new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Background = mark, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center }, Dock.Left));
            }

            row.Children.Add(labelText);
            _body.Children.Add(row);
        }
    }

    IEnumerable<(string Label, string Value, string? Tip, IBrush? Mark)> NowRows()
    {
        var latest = _monitor.Latest;
        if (_monitor.Link is not null && latest is { } now)
        {
            yield return ("Data", $"{LinkMonitor.Rate(now.ReceivedPerSecond)} in, {LinkMonitor.Rate(now.SentPerSecond)} out", null, null);
            if (_monitor.CountsUpdates)
            {
                yield return ("Decoding", $"{now.BusyShare:P0} of the time", "How much of each second this computer spends turning what arrives into pictures", null);
            }
        }

        if (latest is not null && latest.Value.RoundTrip is null)
        {
            yield return ("Round trip", _monitor.Link is null ? "not measured" : "not measured here", RoundTripWhy(), null);
        }

        if (_monitor.Session is IFrameBufferSession frames && frames.Width > 0)
        {
            yield return ("Desktop", $"{frames.Width} × {frames.Height}", null, null);
        }
    }

    IEnumerable<(string Label, string Value, string? Tip, IBrush? Mark)> SessionRows()
    {
        if (_monitor.ConnectedAt is { } at)
        {
            yield return ("Connected for", LinkMonitor.Duration(DateTimeOffset.Now - at), null, null);
        }

        // With its steps the connect time gets a section of its own.
        if (_monitor.Link?.ConnectTime is { } connect && _monitor.ConnectSteps().Count == 0)
        {
            yield return ("Connecting took", LinkMonitor.Milliseconds(connect), null, null);
        }

        if (_monitor.Link is not null)
        {
            var totals = _monitor.Totals;
            yield return ("Moved", $"{LinkMonitor.Size(totals.Received)} in, {LinkMonitor.Size(totals.Sent)} out", null, null);
        }

        if (_monitor.ConnectedAt is not null || _monitor.Drops > 0)
        {
            yield return ("Drops", _monitor.DropsText(), "Times the connection was lost in this tab", null);
        }

        if (_monitor.Link?.LastResize is { } resize)
        {
            yield return ("Last resize", LinkMonitor.Milliseconds(resize), null, null);
        }
    }

    /// <summary>The newest events first, each with the time it happened.</summary>
    IEnumerable<(string Label, string Value, IBrush? Brush)> Recent() =>
        _monitor.Events.Reverse().Take(RecentShown)
            .Select(e => ($"{e.At.ToLocalTime().ToString("t", CultureInfo.CurrentCulture)} {e.Text}", e.Value ?? string.Empty, (IBrush?)MarkBrush(e.Kind)));

    static IBrush MarkBrush(LinkEventKind kind) => kind switch
    {
        LinkEventKind.Connected => Palette.StateUp,
        LinkEventKind.Lost => Palette.Danger,
        LinkEventKind.Copied => VioletBrush,
        _ => GrayBrush,
    };

    /// <summary>The events in the graph's window, as seconds before now with their color.</summary>
    IReadOnlyList<(double SecondsAgo, IBrush Brush)> Marks()
    {
        var now = DateTimeOffset.Now;
        return _monitor.Events
            .Select(e => ((now - e.At).TotalSeconds, MarkBrush(e.Kind)))
            .Where(m => m.TotalSeconds >= 0 && m.TotalSeconds < LinkMonitor.HistoryLength)
            .ToList();
    }

    internal static IBrush StepBrush(string step) => step switch
    {
        "Network" => GrayBrush,
        "Security" => VioletBrush,
        "Sign-in" => Palette.Warning,
        "Session setup" => Palette.Accent,
        "First screen" => Palette.StateUp,
        _ => DarkGrayBrush,
    };

    static string StepTip(string step) => step switch
    {
        "Network" => "Finding the computer and opening the connection",
        "Security" => "Agreeing on encryption and checking the certificate, including any time spent answering about it",
        "Sign-in" => "The server checking the login. A slow one usually waits on a domain controller or a new user profile",
        "Session setup" => "The server getting the session ready: channels, licensing, the desktop",
        "First screen" => "From the session starting to the first picture arriving",
        _ => "Time the steps don't cover, such as a server that sent the connection on to another, or opening a tunnel",
    };

    /// <summary>One bar, split by how long each step took.</summary>
    static Grid StepsBar(IReadOnlyList<ConnectStep> steps)
    {
        double total = Math.Max(1e-9, steps.Sum(s => s.Time.TotalMilliseconds));
        var bar = new Grid { Height = 6, Margin = new Thickness(0, 2, 0, 4), ClipToBounds = true };
        for (int i = 0; i < steps.Count; i++)
        {
            // Every step stays visible, however short.
            double share = Math.Max(steps[i].Time.TotalMilliseconds / total, 0.015);
            bar.ColumnDefinitions.Add(new ColumnDefinition(share, GridUnitType.Star));
            var part = new Border
            {
                Background = StepBrush(steps[i].Name),
                CornerRadius = new CornerRadius(i == 0 ? 3 : 0, i == steps.Count - 1 ? 3 : 0, i == steps.Count - 1 ? 3 : 0, i == 0 ? 3 : 0),
            };
            ToolTip.SetTip(part, $"{steps[i].Name}: {LinkMonitor.Milliseconds(steps[i].Time)}");
            Grid.SetColumn(part, i);
            bar.Children.Add(part);
        }

        return bar;
    }

    static WrapPanel StepsLegend(IReadOnlyList<ConnectStep> steps)
    {
        var legend = new WrapPanel();
        foreach (var step in steps)
        {
            var entry = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                Margin = new Thickness(0, 0, 12, 2),
                Children =
                {
                    new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(2), Background = StepBrush(step.Name), VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = $"{step.Name} {LinkMonitor.Milliseconds(step.Time)}", FontSize = 11, Foreground = SoftBrush },
                },
            };
            ToolTip.SetTip(entry, StepTip(step.Name));
            legend.Children.Add(entry);
        }

        return legend;
    }

    string RoundTripWhy() => _monitor.Link is null
        ? "This kind of session doesn't measure it."
        : "Through an SSH tunnel or jump host it would only measure this computer, so it's left out. It also needs a system that reports it.";

    /// <summary>Orrery's version with the commit it was built from, short, so a report says which build it came from.</summary>
    internal static string Version()
    {
        var assembly = typeof(ConnectionCard).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString() ?? "unknown";
        int plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 && version.Length > plus + 10 ? version[..(plus + 10)] : version;
    }

    /// <summary>Everything on the card as text.</summary>
    internal string Details()
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"{_protocol} to {_monitor.Session.Target.Host}:{_monitor.Session.Target.Port}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Orrery {Version()}");
        text.AppendLine(CultureInfo.InvariantCulture, $"{_monitor.Verdict()}: {Figures()}");
        foreach (var hint in _monitor.Hints())
        {
            text.AppendLine(hint);
        }

        Section(text, "Now", NowRows());
        Section(text, "This session", SessionRows());
        var steps = _monitor.ConnectSteps();
        if (steps.Count > 0 && _monitor.Link?.ConnectTime is { } total)
        {
            text.AppendLine($"Connecting took {LinkMonitor.Milliseconds(total)}: "
                + string.Join(", ", steps.Select(s => $"{s.Name} {LinkMonitor.Milliseconds(s.Time)}")));
        }

        if (_monitor.Events.Count > 0)
        {
            text.AppendLine("Recent:");
            foreach (var e in _monitor.Events.Reverse())
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"  {e.At.ToLocalTime():HH:mm:ss} {e.Text}{(e.Value is { } v ? $" ({v})" : string.Empty)}");
            }
        }

        return text.ToString();
    }

    static void Section(StringBuilder text, string heading, IEnumerable<(string Label, string Value, string? Tip, IBrush? Mark)> rows)
    {
        var list = rows.ToList();
        if (list.Count == 0)
        {
            return;
        }

        text.AppendLine(heading + ":");
        foreach (var (label, value, _, _) in list)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  {label}: {value}");
        }
    }
}

/// <summary>
/// Five minutes of the connection as one graph: the main figure as a line, the round trip as a dashed one, each scaled
/// to its own highest value, newest on the right, with what happened marked as dashed upright lines.
/// </summary>
sealed class LinkGraph : Control
{
    IReadOnlyList<double> _primary = [], _secondary = [];
    IReadOnlyList<(double SecondsAgo, IBrush Brush)> _marks = [];

    public LinkGraph()
    {
        Height = 46;
        Margin = new Thickness(0, 2, 0, 0);
        Name = "ConnectionGraph";
    }

    public IReadOnlyList<double> Primary
    {
        get => _primary;
        set { _primary = value; InvalidateVisual(); }
    }

    public IReadOnlyList<double> Secondary
    {
        get => _secondary;
        set { _secondary = value; InvalidateVisual(); }
    }

    public IReadOnlyList<(double SecondsAgo, IBrush Brush)> Marks
    {
        get => _marks;
        set { _marks = value; InvalidateVisual(); }
    }

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        context.DrawLine(new Pen(Pane.LineBrush, 1), new Point(0, size.Height - 0.5), new Point(size.Width, size.Height - 0.5));
        if (size.Width <= 0)
        {
            return;
        }

        double step = size.Width / (LinkMonitor.HistoryLength - 1);
        foreach (var (ago, brush) in _marks)
        {
            double x = Math.Round(size.Width - ago * step) + 0.5;
            context.DrawLine(new Pen(brush, 1, new DashStyle([2, 2], 0)), new Point(x, 4), new Point(x, size.Height - 1));
            context.DrawEllipse(brush, null, new Point(x, 3), 2.5, 2.5);
        }

        // The round trip reaches the top at slow, so a few milliseconds' wobble on a good link stays a flat line low down.
        Line(context, _secondary, step, new Pen(new SolidColorBrush(Color.Parse("#888780")), 1, new DashStyle([3, 2], 0)), LinkMonitor.SlowRoundTrip.TotalMilliseconds);
        Line(context, _primary, step, new Pen(Palette.Accent, 1.5, lineJoin: PenLineJoin.Round), 1e-9);
    }

    /// <param name="atLeast">The least the top of the graph stands for.</param>
    void Line(DrawingContext context, IReadOnlyList<double> values, double step, IPen pen, double atLeast)
    {
        var size = Bounds.Size;
        if (values.Count < 2)
        {
            return;
        }

        double max = Math.Max(values.Max(), atLeast);
        double left = size.Width - step * (values.Count - 1);
        double top = 8, height = size.Height - top - 2;
        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            for (int i = 0; i < values.Count; i++)
            {
                var point = new Point(left + i * step, top + height - height * Math.Clamp(values[i] / max, 0, 1));
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

        context.DrawGeometry(null, pen, line);
    }
}
