using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Orrery.App.Sessions;
using Orrery.Core.Settings;
using Xunit;
using Vision = Orrery.Core.Settings.ColorVision;

namespace Orrery.App.Tests;

/// <summary>
/// Colors that carry meaning must stay apart with color blindness, and text must be readable. The color-blind sets
/// must pass everything; the standard sets' shortfalls are listed so a change to them is a decision, not an accident.
/// </summary>
public class ColorAccessibilityTests(ITestOutputHelper output)
{
    /// <summary>
    /// CIEDE2000 needed between two meaning-carrying colors under each simulated deficiency. Around 10 is a clear
    /// difference side by side; small marks such as a tab's state dot, seen one at a time, need more.
    /// </summary>
    const double MinimumDifference = 20;

    const double TextContrast = 4.5, MarkContrast = 3;

    static readonly Deficiency[] Deficiencies = [Deficiency.None, Deficiency.Protanopia, Deficiency.Deuteranopia, Deficiency.Tritanopia];

    // Where these colors are drawn: panels and dialogs, the page and status bar, the session area, buttons,
    // the danger button, the selected tree row.
    static readonly (string Name, Color Color)[] TextBackgrounds =
    [
        ("panel", Color.Parse("#14161B")), ("page", Color.Parse("#0E1014")), ("session", Color.Parse("#0B0D10")),
        ("button", Color.Parse("#181B21")),
    ];

    static readonly (string Name, Color Color)[] MarkBackgrounds =
    [
        ("panel", Color.Parse("#14161B")), ("page", Color.Parse("#0E1014")), ("selected row", Color.Parse("#232833")),
    ];

    static readonly Color DangerButton = Color.Parse("#2A1A1D");
    static readonly Color TerminalBackground = Color.Parse("#0B0D10");

    static IEnumerable<(string Pair, Color A, Color B)> Pairs(UiColors c) =>
    [
        ("up/down", c.StateUp, c.StateDown),
        ("up/connecting", c.StateUp, c.StateConnecting),
        ("connecting/down", c.StateConnecting, c.StateDown),
        ("up/ended", c.StateUp, c.StateEnded),
        ("connecting/ended", c.StateConnecting, c.StateEnded),
        ("down/ended", c.StateDown, c.StateEnded),
        ("danger/warning", c.Danger, c.Warning),
        ("success/danger", c.Success, c.Danger),
        ("success/warning", c.Success, c.Warning),
    ];

    static IEnumerable<(string Pair, Color A, Color B)> Pairs(TerminalPalette p) =>
    [
        ("red/green", p.Ansi[1], p.Ansi[2]),
        ("bright red/bright green", p.Ansi[9], p.Ansi[10]),
        ("red/yellow", p.Ansi[1], p.Ansi[3]),
        ("green/yellow", p.Ansi[2], p.Ansi[3]),
    ];

    static readonly string[] AnsiNames =
    [
        "black", "red", "green", "yellow", "blue", "magenta", "cyan", "white",
        "bright black", "bright red", "bright green", "bright yellow", "bright blue", "bright magenta", "bright cyan", "bright white",
    ];

    static List<string> Problems(UiColors c)
    {
        var problems = new List<string>();
        foreach (var (pair, a, b) in Pairs(c))
        {
            foreach (var deficiency in Deficiencies)
            {
                double difference = ColorVision.Difference(a, b, deficiency);
                if (difference < MinimumDifference)
                {
                    problems.Add($"{pair} {deficiency} ΔE {difference:0.0}");
                }
            }
        }

        foreach (var (name, color) in new[] { ("danger", c.Danger), ("warning", c.Warning), ("success", c.Success), ("accent", c.Accent) })
        {
            foreach (var (background, back) in TextBackgrounds)
            {
                if (ColorVision.Contrast(color, back) is var ratio && ratio < TextContrast)
                {
                    problems.Add($"{name} text on {background} {ratio:0.00}:1");
                }
            }
        }

        if (ColorVision.Contrast(c.Danger, DangerButton) is var onButton && onButton < TextContrast)
        {
            problems.Add($"danger text on danger button {onButton:0.00}:1");
        }

        foreach (var (name, color) in new[] { ("up", c.StateUp), ("connecting", c.StateConnecting), ("down", c.StateDown), ("ended", c.StateEnded) })
        {
            foreach (var (background, back) in MarkBackgrounds)
            {
                if (ColorVision.Contrast(color, back) is var ratio && ratio < MarkContrast)
                {
                    problems.Add($"{name} mark on {background} {ratio:0.00}:1");
                }
            }
        }

        return problems;
    }

    static List<string> Problems(TerminalPalette p)
    {
        var problems = new List<string>();
        foreach (var (pair, a, b) in Pairs(p))
        {
            foreach (var deficiency in Deficiencies)
            {
                double difference = ColorVision.Difference(a, b, deficiency);
                if (difference < MinimumDifference)
                {
                    problems.Add($"{pair} {deficiency} ΔE {difference:0.0}");
                }
            }
        }

        // Black is a background color; everything else is text on the terminal's background.
        for (int i = 1; i < 16; i++)
        {
            if (ColorVision.Contrast(p.Ansi[i], TerminalBackground) is var ratio && ratio < TextContrast)
            {
                problems.Add($"{AnsiNames[i]} text {ratio:0.00}:1");
            }
        }

        // A bright color should look brighter than its normal one, or bold text loses its emphasis.
        for (int i = 1; i < 8; i++)
        {
            if (ColorVision.Contrast(p.Ansi[i + 8], TerminalBackground) <= ColorVision.Contrast(p.Ansi[i], TerminalBackground))
            {
                problems.Add($"{AnsiNames[i + 8]} isn't brighter than {AnsiNames[i]}");
            }
        }

        return problems;
    }

    void Report(string title, IEnumerable<(string Pair, Color A, Color B)> pairs, List<string> problems)
    {
        output.WriteLine(title);
        foreach (var (pair, a, b) in pairs)
        {
            output.WriteLine($"  {pair,-24} " + string.Join("  ", Deficiencies.Select(d => $"{d} {ColorVision.Difference(a, b, d),5:0.0}")));
        }

        output.WriteLine(problems.Count == 0 ? "  No problems." : "  Problems:\n    " + string.Join("\n    ", problems));
    }

    [AvaloniaFact]
    public void Color_blind_ui_colors_stay_apart_and_readable()
    {
        var problems = Problems(Palette.ColorBlind);
        Report("Color-blind UI palette", Pairs(Palette.ColorBlind), problems);
        Assert.Empty(problems);
    }

    [AvaloniaFact]
    public void Clearer_ui_colors_stay_apart_and_readable()
    {
        var problems = Problems(Palette.Clearer);
        Report("Clearer UI palette", Pairs(Palette.Clearer), problems);
        Assert.Empty(problems);
    }

    [AvaloniaFact]
    public void Color_blind_terminal_colors_stay_apart_and_readable()
    {
        var problems = Problems(TerminalPalette.ColorBlind);
        Report("Color-blind terminal palette", Pairs(TerminalPalette.ColorBlind), problems);
        Assert.Empty(problems);
    }

    /// <summary>
    /// The terminal palettes tuned for one kind of color blindness must also keep every pair of the six colors apart,
    /// normal and bright, for that kind and for normal vision. Less than <see cref="MinimumDifference"/>: six colors
    /// all bright enough to read can't all be that far apart for one kind.
    /// </summary>
    const double TunedPairDifference = 15;

    static List<string> EveryPairProblems(TerminalPalette p, Deficiency deficiency)
    {
        var problems = new List<string>();
        foreach (int offset in new[] { 0, 8 })
        {
            for (int i = 1; i < 7; i++)
            {
                for (int j = i + 1; j < 7; j++)
                {
                    foreach (var d in new[] { Deficiency.None, deficiency })
                    {
                        double difference = ColorVision.Difference(p.Ansi[i + offset], p.Ansi[j + offset], d);
                        if (difference < TunedPairDifference)
                        {
                            problems.Add($"{AnsiNames[i + offset]}/{AnsiNames[j + offset]} {d} ΔE {difference:0.0}");
                        }
                    }
                }
            }
        }

        return problems;
    }

    [AvaloniaTheory]
    [InlineData(Vision.Protanopia, Deficiency.Protanopia)]
    [InlineData(Vision.Deuteranopia, Deficiency.Deuteranopia)]
    [InlineData(Vision.Tritanopia, Deficiency.Tritanopia)]
    public void Terminal_colors_tuned_for_a_color_vision_stay_apart_for_it(Vision vision, Deficiency deficiency)
    {
        var palette = TerminalPalette.For(TerminalColorScheme.ColorBlind, vision);
        Assert.NotSame(TerminalPalette.ColorBlind, palette);
        var problems = Problems(palette).Concat(EveryPairProblems(palette, deficiency)).ToList();
        Report($"{vision} terminal palette", Pairs(palette), problems);
        Assert.Empty(problems);

        // Why it's there: the general color-blind palette lets some pairs run together for this kind.
        var general = EveryPairProblems(TerminalPalette.ColorBlind, deficiency);
        output.WriteLine("  The color-blind palette instead: " + string.Join(", ", general));
        Assert.NotEmpty(general);
    }

    [AvaloniaFact]
    public void The_interface_colors_a_color_vision_picks_suit_every_kind()
    {
        // Settings switches to the color-blind set for any kind, so that set has to pass for all three. It does (as
        // does Clearer), so there are no interface sets per kind; the terminal ones are tuned instead.
        Assert.Empty(Problems(Palette.ColorBlind));

        Assert.Same(TerminalPalette.Standard, TerminalPalette.For(TerminalColorScheme.Standard, Vision.Deuteranopia));
        Assert.Same(TerminalPalette.ColorBlind, TerminalPalette.For(TerminalColorScheme.ColorBlind, Vision.None));
    }

    [AvaloniaFact]
    public void Standard_ui_colors_fall_short_only_where_known()
    {
        var problems = Problems(Palette.Standard);
        Report("Standard UI palette", Pairs(Palette.Standard), problems);
        Assert.Equal(StandardUiProblems, problems);
    }

    [AvaloniaFact]
    public void Standard_terminal_colors_fall_short_only_where_known()
    {
        var problems = Problems(TerminalPalette.Standard);
        Report("Standard terminal palette", Pairs(TerminalPalette.Standard), problems);
        Assert.Equal(StandardTerminalProblems, problems);
    }

    // Kept as they are for now: the color-blind settings fix them, and changing the defaults is the owner's call.
    static readonly string[] StandardUiProblems =
    [
        "up/down Protanopia ΔE 16.4",
        "up/down Deuteranopia ΔE 3.2",
        "up/connecting Protanopia ΔE 8.2",
        "up/connecting Deuteranopia ΔE 11.5",
        "connecting/down Deuteranopia ΔE 13.9",
        "connecting/down Tritanopia ΔE 8.2",
        "danger/warning Protanopia ΔE 18.4",
        "danger/warning Deuteranopia ΔE 12.1",
        "danger/warning Tritanopia ΔE 6.8",
        "success/danger Protanopia ΔE 16.4",
        "success/danger Deuteranopia ΔE 3.2",
        "success/warning Protanopia ΔE 4.5",
        "success/warning Deuteranopia ΔE 10.0",
        "ended mark on panel 2.99:1",
        "ended mark on selected row 2.44:1",
    ];

    static readonly string[] StandardTerminalProblems =
    [
        "red/green Deuteranopia ΔE 9.3",
        "bright red/bright green Deuteranopia ΔE 11.7",
        "red/yellow Deuteranopia ΔE 15.5",
        "red/yellow Tritanopia ΔE 20.0",
        "green/yellow Protanopia ΔE 1.9",
        "green/yellow Deuteranopia ΔE 6.2",
        "bright black text 3.22:1",
    ];

    [AvaloniaFact]
    public void Protocol_badges_are_readable()
    {
        foreach (var protocol in Enum.GetValues<Orrery.Core.Connections.Protocol>())
        {
            var fore = ((ISolidColorBrush)Orrery.App.Connections.ProtocolStyle.Foreground(protocol)).Color;
            var back = ((ISolidColorBrush)Orrery.App.Connections.ProtocolStyle.Background(protocol)).Color;
            double ratio = ColorVision.Contrast(fore, back);
            output.WriteLine($"{protocol} badge {ratio:0.00}:1");
            Assert.True(ratio >= TextContrast, $"{protocol} badge {ratio:0.00}:1");
        }
    }

    // ---- The color science itself ----

    [AvaloniaFact]
    public void Ciede2000_matches_published_test_pairs()
    {
        // Sharma, Wu and Dalal (2005), pairs 1, 7 and 17.
        Assert.Equal(2.0425, ColorVision.DeltaE2000((50, 2.6772, -79.7751), (50, 0, -82.7485)), 4);
        Assert.Equal(2.3669, ColorVision.DeltaE2000((50, 0, 0), (50, -1, 2)), 4);
        Assert.Equal(27.1492, ColorVision.DeltaE2000((50, 2.5, 0), (73, 25, -18)), 4);
    }

    [AvaloniaFact]
    public void Simulation_keeps_grays_and_merges_red_with_green()
    {
        foreach (var deficiency in Deficiencies)
        {
            Assert.True(ColorVision.Difference(Colors.White, Color.Parse("#FEFEFE"), deficiency) < 1);
            Assert.True(ColorVision.Difference(Color.Parse("#808080"), Color.Parse("#7F7F7F"), deficiency) < 1);
        }

        // A red and a green of about the same lightness: far apart normally, close for red-green color blindness.
        Color red = Color.Parse("#C86450"), green = Color.Parse("#7D8C3C");
        Assert.True(ColorVision.Difference(red, green, Deficiency.None) > 30);
        Assert.True(ColorVision.Difference(red, green, Deficiency.Deuteranopia) < 10);
        Assert.True(ColorVision.Difference(red, green, Deficiency.Protanopia) < 15);
    }

    [AvaloniaFact]
    public void Contrast_matches_wcag()
    {
        Assert.Equal(21, ColorVision.Contrast(Colors.Black, Colors.White), 2);
        Assert.Equal(4.54, ColorVision.Contrast(Color.Parse("#767676"), Colors.White), 2);
    }
}
