using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Orrery.App.Connections;
using Orrery.App.Controls;

namespace Orrery.App.Sessions;

/// <summary>
/// The tab being dragged, lifted off the strip: drawn over the window, so the strip's scrolling doesn't cut it off,
/// and following the pointer. Pulled far enough to pop out, it opens into a small picture of the window it will
/// become. It only shows; the drag itself stays with whoever started it.
/// </summary>
sealed class TabGhost
{
    /// <summary>The picture of the window a tab pulled off the strip will become.</summary>
    public const double WindowWidth = 240, WindowHeight = 150;

    /// <summary>Where the pointer holds the picture of a window: on its bar, near the title, as a popout lands.</summary>
    public static readonly Vector WindowGrip = new(60, 16);

    static readonly TimeSpan MorphTime = TimeSpan.FromMilliseconds(140);

    readonly OverlayLayer _layer;
    readonly Border _ghost;
    readonly Border _chip;
    readonly Border _body;
    readonly double _chipWidth;
    bool _asWindow;

    TabGhost(OverlayLayer layer, SessionTab tab, double chipWidth)
    {
        _layer = layer;
        _chipWidth = chipWidth;
        var title = new TextBlock { Text = tab.Origin.Title, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 200, TextTrimming = TextTrimming.CharacterEllipsis };
        Themed(title, TextBlock.ForegroundProperty, "OrreryText");
        _chip = new Border
        {
            Height = 32,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 0),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new StateGlyph { State = tab.Session.State, VerticalAlignment = VerticalAlignment.Center },
                    title,
                    new TextBlock
                    {
                        Text = tab.Origin.Label,
                        FontFamily = Dialogs.MonoFont,
                        FontSize = 10,
                        Foreground = ProtocolStyle.Foreground(tab.Origin.Protocol),
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            },
        };
        Themed(_chip, Border.BackgroundProperty, "OrreryBackground");
        Themed(_chip, Border.BorderBrushProperty, "OrreryBorder");

        _body = new Border
        {
            IsVisible = false,
            CornerRadius = new CornerRadius(0, 0, 8, 8),
            Background = new SolidColorBrush(Color.Parse("#0B0D10")),
            Child = Themed(new TextBlock
            {
                Text = "Opens in its own window",
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            }, TextBlock.ForegroundProperty, "OrreryMuted"),
        };

        // The outline is drawn over the parts rather than around them, so it keeps its rounded corners at every size.
        var outline = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
        Themed(outline, Border.BorderBrushProperty, "OrreryAccent");
        _ghost = new Border
        {
            Width = chipWidth,
            Height = 32,
            CornerRadius = new CornerRadius(8),
            IsHitTestVisible = false,
            BoxShadow = BoxShadows.Parse("0 8 24 0 #80000000"),
            Transitions =
            [
                new DoubleTransition { Property = Layoutable.WidthProperty, Duration = MorphTime },
                new DoubleTransition { Property = Layoutable.HeightProperty, Duration = MorphTime },
            ],
            Child = new Panel { Children = { new DockPanel { Children = { Docked(_chip, Dock.Top), _body } }, outline } },
        };
        Themed(_ghost, Border.BackgroundProperty, "OrreryBackground");
        _layer.Children.Add(_ghost);
    }

    /// <summary>
    /// Lifts <paramref name="tab"/> over <paramref name="window"/>, <paramref name="width"/> wide as a tab; null where
    /// the window has nowhere to draw it.
    /// </summary>
    public static TabGhost? Show(Visual window, SessionTab tab, double width) =>
        OverlayLayer.GetOverlayLayer(window) is { } layer ? new TabGhost(layer, tab, Math.Max(width, 80)) : null;

    /// <summary>Whether it shows as the window the tab will become, rather than as a tab.</summary>
    public bool AsWindow
    {
        get => _asWindow;
        set
        {
            if (_asWindow == value)
            {
                return;
            }

            _asWindow = value;
            _body.IsVisible = value;

            // As a window, the tab is its bar: the color of a popout's, with a line under it.
            _chip.CornerRadius = value ? new CornerRadius(8, 8, 0, 0) : new CornerRadius(8);
            _chip.BorderThickness = value ? new Thickness(0, 0, 0, 1) : default;
            Themed(_chip, Border.BackgroundProperty, value ? "OrreryPanel" : "OrreryBackground");
            _ghost.Width = value ? WindowWidth : _chipWidth;
            _ghost.Height = value ? WindowHeight : 32;
        }
    }

    /// <summary>Puts its top left corner at <paramref name="point"/>, from the top left of <paramref name="relativeTo"/>.</summary>
    public void MoveTo(Visual relativeTo, Point point)
    {
        if (relativeTo.TranslatePoint(point, _layer) is { } at)
        {
            Canvas.SetLeft(_ghost, at.X);
            Canvas.SetTop(_ghost, at.Y);
        }
    }

    public void Remove() => _layer.Children.Remove(_ghost);

    static Control Docked(Control control, Dock dock)
    {
        DockPanel.SetDock(control, dock);
        return control;
    }

    static T Themed<T>(T control, AvaloniaProperty property, string key) where T : Control
    {
        control.Bind(property, control.GetResourceObservable(key));
        return control;
    }
}
