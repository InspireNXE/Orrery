using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Orrery.Core.Sessions;

namespace Orrery.App.Controls;

/// <summary>
/// A session's state as a small mark whose shape says it as well as its color: a filled dot when connected, a broken
/// ring while connecting, a cross when it failed, and an empty ring when it ended or no session is open.
/// </summary>
public sealed class StateGlyph : Control
{
    public static readonly StyledProperty<SessionState?> StateProperty =
        AvaloniaProperty.Register<StateGlyph, SessionState?>(nameof(State));

    /// <summary>Connected, but the link is slow (a long round trip): the dot turns the warning color.</summary>
    public static readonly StyledProperty<bool> SlowProperty =
        AvaloniaProperty.Register<StateGlyph, bool>(nameof(Slow));

    static StateGlyph() => AffectsRender<StateGlyph>(StateProperty, SlowProperty);

    public StateGlyph()
    {
        Width = Height = 9;
        AutomationProperties.SetName(this, Describe(null));
    }

    public SessionState? State
    {
        get => GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    public bool Slow
    {
        get => GetValue(SlowProperty);
        set => SetValue(SlowProperty, value);
    }

    /// <summary>The state in words, for tooltips and screen readers.</summary>
    public static string Describe(SessionState? state) => state switch
    {
        SessionState.Connected => "Connected",
        SessionState.Connecting => "Connecting",
        SessionState.Failed => "Failed",
        SessionState.Disconnected => "Disconnected",
        _ => "Not connected",
    };

    public static IBrush BrushFor(SessionState? state) => state switch
    {
        SessionState.Connected => Palette.StateUp,
        SessionState.Connecting => Palette.StateConnecting,
        SessionState.Failed => Palette.StateDown,
        _ => Palette.StateEnded,
    };

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == StateProperty || change.Property == SlowProperty)
        {
            AutomationProperties.SetName(this, State == SessionState.Connected && Slow ? "Connected, slow" : Describe(State));
        }
    }

    // The palette changes its brushes in place; redrawing makes sure the new color shows at once.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Palette.Changed += OnPaletteChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Palette.Changed -= OnPaletteChanged;
    }

    void OnPaletteChanged(object? sender, EventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        double size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0)
        {
            return;
        }

        var brush = State == SessionState.Connected && Slow ? Palette.Warning : BrushFor(State);
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        double radius = size / 2;
        double stroke = Math.Max(1.2, size / 6);
        var pen = new Pen(brush, stroke, lineCap: PenLineCap.Round);

        switch (State)
        {
            case SessionState.Connected:
                context.DrawEllipse(brush, null, center, radius, radius);
                break;

            case SessionState.Connecting:
                // Three quarters of a ring, like a spinner caught still.
                double r = radius - stroke / 2;
                var arc = new StreamGeometry();
                using (var g = arc.Open())
                {
                    g.BeginFigure(new Point(center.X, center.Y - r), false);
                    g.ArcTo(new Point(center.X - r, center.Y), new Size(r, r), 0, true, SweepDirection.Clockwise);
                    g.EndFigure(false);
                }

                context.DrawGeometry(null, pen, arc);
                break;

            case SessionState.Failed:
                double d = radius * 0.8;
                context.DrawLine(pen, new Point(center.X - d, center.Y - d), new Point(center.X + d, center.Y + d));
                context.DrawLine(pen, new Point(center.X + d, center.Y - d), new Point(center.X - d, center.Y + d));
                break;

            default:
                double ring = radius - 0.6;
                context.DrawEllipse(null, new Pen(brush, 1.2), center, ring, ring);
                break;
        }
    }
}
