using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Orrery.App.Controls;
using Orrery.Core.Connections;
using Orrery.Core.Sessions;

namespace Orrery.App.Connections;

/// <summary>A display of this computer as the picker shows it: where it is, in pixels, and its name if the system gives one.</summary>
readonly record struct LocalDisplay(ScreenArea Area, string? Name);

/// <summary>
/// This computer's displays laid out as the system arranges them, each a toggle, for the connection's "Full screen on"
/// chosen displays. Several can be chosen on Windows only, which is the one place a window can cover several; elsewhere
/// choosing one clears the rest. Chosen displays that aren't connected now are kept, so saving doesn't drop them.
/// </summary>
sealed class DisplayPicker : UserControl
{
    static readonly IBrush OnBrush = new SolidColorBrush(Color.Parse("#1D3256")).ToImmutable();
    static readonly IBrush OnBorderBrush = new SolidColorBrush(Color.Parse("#4C8DFF")).ToImmutable();
    static readonly IBrush OffBrush = new SolidColorBrush(Color.Parse("#171B22")).ToImmutable();
    static readonly IBrush OffBorderBrush = new SolidColorBrush(Color.Parse("#3A4352")).ToImmutable();
    static readonly IBrush MapBrush = new SolidColorBrush(Color.Parse("#11151B")).ToImmutable();

    readonly Func<IReadOnlyList<LocalDisplay>> _displays;
    readonly Map _map = new();
    readonly StackPanel _names = new() { Spacing = 3 };
    readonly TextBlock _note = new() { FontSize = 11, Foreground = Dialogs.MutedBrush, TextWrapping = TextWrapping.Wrap };
    readonly List<ToggleButton> _toggles = [];
    IReadOnlyList<LocalDisplay> _shown = [];
    List<ChosenDisplay> _absent = [];
    bool _updating;

    /// <param name="displays">This computer's displays; by default the window's screens.</param>
    /// <param name="several">Whether more than one can be chosen; by default on Windows only.</param>
    public DisplayPicker(Func<IReadOnlyList<LocalDisplay>>? displays = null, bool? several = null)
    {
        _displays = displays ?? (() => Screens(this));
        AllowsSeveral = several ?? OperatingSystem.IsWindows();
        AutomationProperties.SetName(this, "Displays for full screen");
        Content = new Border
        {
            Background = MapBrush,
            BorderBrush = Pane.LineBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Child = new StackPanel { Spacing = 10, Children = { _map, _names, _note } },
        };
    }

    /// <summary>Raised when the user picks or clears a display.</summary>
    public event EventHandler? Changed;

    public bool AllowsSeveral { get; }

    /// <summary>The chosen displays: those picked here, then any chosen before that aren't connected now.</summary>
    public List<ChosenDisplay> Chosen
    {
        get => [.. _toggles.Select((t, i) => (t, i)).Where(p => p.t.IsChecked == true).Select(p => ChosenDisplay.Of(_shown[p.i].Area, _shown[p.i].Name)), .. _absent];
        set
        {
            Refresh();
            var choice = new FullScreenChoice(FullScreenOn.ChosenDisplays, value);
            var picked = choice.Pick(_shown.Select(d => d.Area).ToList(), _shown.Select(d => d.Name).ToList(), out _);
            _absent = value.Where(c => !_shown.Any(d => c.Is(d.Area)) && !picked.Any(i => _shown[i].Name is { } name && name == c.Name)).ToList();
            _updating = true;
            try
            {
                for (int i = 0; i < _toggles.Count; i++)
                {
                    _toggles[i].IsChecked = picked.Contains(i);
                }
            }
            finally
            {
                _updating = false;
            }

            ShowChoice();
        }
    }

    /// <summary>"Displays 1 and 3", "Display 2", or "None", for the row's value.</summary>
    public string Describe()
    {
        var numbers = _toggles.Select((t, i) => (t, i)).Where(p => p.t.IsChecked == true).Select(p => (p.i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();
        string shown = numbers.Count switch
        {
            0 => _absent.Count > 0 ? "Not connected now" : "None chosen",
            1 => $"Display {numbers[0]}",
            _ => $"Displays {string.Join(", ", numbers[..^1])} and {numbers[^1]}",
        };
        return numbers.Count > 0 && _absent.Count > 0 ? $"{shown}, and {_absent.Count} not connected" : shown;
    }

    /// <summary>Reads the displays again, keeping what's chosen.</summary>
    public void Refresh()
    {
        var displays = _displays();
        if (displays.SequenceEqual(_shown))
        {
            return;
        }

        var chosen = _toggles.Count > 0 ? Chosen : null;
        _shown = displays;
        _toggles.Clear();
        _map.Children.Clear();
        _names.Children.Clear();
        int primary = Math.Max(0, displays.ToList().FindIndex(d => d.Area.IsPrimary));
        foreach (var (display, i) in displays.Select((d, i) => (d, i)))
        {
            string number = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var toggle = new ToggleButton
            {
                Padding = new Thickness(0),
                MinHeight = 0,
                CornerRadius = new CornerRadius(5),
                BorderThickness = new Thickness(1.5),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Content = new StackPanel
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new TextBlock { Text = number, FontWeight = FontWeight.SemiBold, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center },
                        new TextBlock { Text = $"{display.Area.Width}×{display.Area.Height}", FontSize = 10, FontFamily = Dialogs.MonoFont, HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0.85 },
                    },
                },
                Tag = display.Area,
            };
            AutomationProperties.SetName(toggle, $"Display {number}, {display.Area.Width} by {display.Area.Height}{(i == primary ? ", main" : string.Empty)}{(display.Name is { } n ? $", {n}" : string.Empty)}");
            toggle.IsCheckedChanged += (_, _) => OnToggled(toggle);
            _toggles.Add(toggle);
            _map.Children.Add(toggle);
            _names.Children.Add(new TextBlock
            {
                FontSize = 11,
                Foreground = Dialogs.MutedBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Text = $"{number} · {display.Name ?? $"{display.Area.Width}×{display.Area.Height}"}{(i == primary ? " · main" : string.Empty)}",
            });
        }

        if (chosen is not null)
        {
            Chosen = chosen;
        }
        else
        {
            ShowChoice();
        }
    }

    void OnToggled(ToggleButton toggle)
    {
        if (_updating)
        {
            return;
        }

        // Elsewhere than Windows a full-screen window fills one display, so picking one is choosing it.
        if (!AllowsSeveral && toggle.IsChecked == true)
        {
            _updating = true;
            try
            {
                foreach (var other in _toggles.Where(t => t != toggle))
                {
                    other.IsChecked = false;
                }

                _absent.Clear();
            }
            finally
            {
                _updating = false;
            }
        }

        ShowChoice();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Colors the displays and says what the choice does: moved together, refused, or not connected.</summary>
    void ShowChoice()
    {
        foreach (var toggle in _toggles)
        {
            bool on = toggle.IsChecked == true;
            toggle.Background = on ? OnBrush : OffBrush;
            toggle.BorderBrush = on ? OnBorderBrush : OffBorderBrush;
        }

        var picked = _toggles.Select((t, i) => (t, i)).Where(p => p.t.IsChecked == true).Select(p => _shown[p.i].Area).ToList();
        var numbers = _toggles.Select((t, i) => (t, i)).Where(p => p.t.IsChecked == true).Select(p => p.i + 1).ToList();
        string? note = null;
        bool moved = false;
        if (picked.Count > 1 && MonitorLayout.Arrange(picked, out _, out moved, out var whyNot) is null)
        {
            note = $"These can't make one remote desktop: {whyNot}";
        }
        else if (moved)
        {
            note = $"Displays {string.Join(" and ", numbers)} sit side by side on the remote computer. The pointer crosses the displays between them; "
                + "move remote windows across with Win+Shift+arrow.";
        }

        if (!AllowsSeveral)
        {
            // Wayland doesn't let a window pick its display at all; the choice still holds where this connection opens on Windows or macOS.
            note = OperatingSystem.IsLinux()
                ? "Wayland keeps full screen on the display the window is on. The choice holds when this connection opens on Windows or macOS."
                : "A window fills one display here, so full screen is on the one chosen. Several work on Windows.";
        }

        if (_absent.Count > 0)
        {
            note = (note is null ? string.Empty : note + " ") + (_absent.Count == 1
                ? "One chosen display isn't connected now; full screen leaves it out."
                : $"{_absent.Count} chosen displays aren't connected now; full screen leaves them out.");
        }

        _note.Text = note;
        _note.IsVisible = note is not null;
    }

    static IReadOnlyList<LocalDisplay> Screens(Visual visual) =>
        TopLevel.GetTopLevel(visual)?.Screens?.All.Select(Of).ToList() ?? [];

    /// <summary>A screen as a display, with its name unless it's only a device path such as \\.\DISPLAY2.</summary>
    internal static LocalDisplay Of(Avalonia.Platform.Screen screen) => new(
        new ScreenArea(screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height, screen.IsPrimary),
        screen.DisplayName is { Length: > 0 } name && !name.StartsWith(@"\\", StringComparison.Ordinal) ? name : null);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Refresh();
    }

    /// <summary>The displays drawn to scale, where they sit, to fit the width it's given and at most 150 high.</summary>
    sealed class Map : Panel
    {
        const double Tallest = 150;

        (double Scale, double Left, double Top, double Height) Fit(double width)
        {
            var areas = Children.Select(c => (ScreenArea)c.Tag!).ToList();
            if (areas.Count == 0 || width <= 0 || double.IsInfinity(width))
            {
                return (0, 0, 0, 0);
            }

            int left = areas.Min(a => a.X), top = areas.Min(a => a.Y);
            double totalWidth = areas.Max(a => a.Right) - left, totalHeight = areas.Max(a => a.Bottom) - top;
            double scale = Math.Min(width / totalWidth, Tallest / totalHeight);
            return (scale, left, top, totalHeight * scale);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            double width = double.IsInfinity(availableSize.Width) ? 300 : availableSize.Width;
            var (scale, _, _, height) = Fit(width);
            foreach (var child in Children)
            {
                var area = (ScreenArea)child.Tag!;
                child.Measure(new Size(Math.Max(0, area.Width * scale - 2), Math.Max(0, area.Height * scale - 2)));
            }

            return new Size(width, height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var (scale, left, top, height) = Fit(finalSize.Width);
            foreach (var child in Children)
            {
                var area = (ScreenArea)child.Tag!;

                // A 2 px gap shows where one display ends and the next begins.
                child.Arrange(new Rect((area.X - left) * scale + 1, (area.Y - top) * scale + 1, Math.Max(0, area.Width * scale - 2), Math.Max(0, area.Height * scale - 2)));
            }

            return new Size(finalSize.Width, height);
        }
    }
}
