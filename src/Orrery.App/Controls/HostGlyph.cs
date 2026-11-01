using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Orrery.Core.Network;

namespace Orrery.App.Controls;

/// <summary>
/// Whether a connection's host answered the last check, as a small mark whose shape says it as well as its color:
/// a filled dot when it's up, a cross when it's down, and a dashed ring when it wasn't checked. Nothing is drawn when
/// checks are off. It uses the palette's state brushes, so it follows the color-blind setting like
/// <see cref="StateGlyph"/>, which shows the session's state; the tree keeps the two apart by where they sit.
/// </summary>
public sealed class HostGlyph : Control
{
    public static readonly StyledProperty<HostState?> StateProperty =
        AvaloniaProperty.Register<HostGlyph, HostState?>(nameof(State));

    static readonly double[] Dashes = [1.5, 1.5];

    static HostGlyph() => AffectsRender<HostGlyph>(StateProperty);

    public HostGlyph()
    {
        Width = Height = 8;
        AutomationProperties.SetName(this, Describe(null));
    }

    public HostState? State
    {
        get => GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    /// <summary>The state in words, for screen readers.</summary>
    public static string Describe(HostState? state) => state switch
    {
        HostState.Up => "Host up",
        HostState.Down => "Host down",
        HostState.Unknown => "Host not checked",
        _ => "Host checks off",
    };

    public static IBrush BrushFor(HostState? state) => state switch
    {
        HostState.Up => Palette.StateUp,
        HostState.Down => Palette.StateDown,
        _ => Palette.StateEnded,
    };

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == StateProperty)
        {
            AutomationProperties.SetName(this, Describe(State));
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
        if (size <= 0 || State is not { } state)
        {
            return;
        }

        var brush = BrushFor(state);
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        double radius = size / 2;
        switch (state)
        {
            case HostState.Up:
                context.DrawEllipse(brush, null, center, radius, radius);
                break;

            case HostState.Down:
                double d = radius * 0.75;
                var pen = new Pen(brush, Math.Max(1.2, size / 5), lineCap: PenLineCap.Round);
                context.DrawLine(pen, new Point(center.X - d, center.Y - d), new Point(center.X + d, center.Y + d));
                context.DrawLine(pen, new Point(center.X + d, center.Y - d), new Point(center.X - d, center.Y + d));
                break;

            default:
                double ring = radius - 0.6;
                context.DrawEllipse(null, new Pen(brush, 1.2, new DashStyle(Dashes, 0)), center, ring, ring);
                break;
        }
    }
}
