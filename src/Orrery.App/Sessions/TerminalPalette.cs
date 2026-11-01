using Avalonia.Media;
using Orrery.Core.Settings;

namespace Orrery.App.Sessions;

/// <summary>
/// The 16 ANSI colours a terminal draws with: black, red, green, yellow, blue, magenta, cyan, white, then the bright
/// eight in the same order. Text and background colours stay the same in every scheme.
/// </summary>
sealed class TerminalPalette
{
    TerminalPalette(params string[] colours) => Ansi = [.. colours.Select(c => Color.Parse(c))];

    public IReadOnlyList<Color> Ansi { get; }

    /// <summary>A dark-friendly take on the usual colours.</summary>
    public static TerminalPalette Standard { get; } = new(
        "#1B1E24", "#E06C75", "#98C379", "#E5C07B", "#61AFEF", "#C678DD", "#56B6C2", "#C8CCD4",
        "#5C6370", "#FF7A85", "#B5E890", "#FFD68A", "#82C4FF", "#DE9BF0", "#7FD8E3", "#FFFFFF");

    /// <summary>
    /// After Okabe and Ito: red is vermilion and green is bluish green, so the two differ in lightness and in
    /// blue-yellow as well as red-green. Bright black is light enough to read.
    /// </summary>
    public static TerminalPalette ColourBlind { get; } = new(
        "#1B1E24", "#E0603A", "#3DB8A0", "#F0E442", "#6E8EF5", "#D884B4", "#4FC3E8", "#C8CCD4",
        "#7A8292", "#FF8048", "#6FE0C8", "#FAF39A", "#93AAFA", "#EBA5CC", "#8BD8F5", "#FFFFFF");

    // Tuned from ColourBlind for one kind of colour blindness each, with red, green and yellow kept apart for all
    // three. Every pair of the six colours stays apart for normal vision and for that kind, where ColourBlind lets a
    // few run together (green and magenta for deuteranopia, green and cyan for tritanopia).

    /// <summary>For protanopia: a bluer blue and a pinker magenta, so neither meets cyan or each other.</summary>
    public static TerminalPalette Protanopia { get; } = new(
        "#1B1E24", "#E0603A", "#40BE9E", "#F0E442", "#5778FF", "#CA728E", "#55CBEF", "#C8CCD4",
        "#7A8292", "#EE7138", "#65FFEC", "#FEF697", "#8298FF", "#E08DA8", "#6BE0FF", "#FFFFFF");

    /// <summary>For deuteranopia: a lighter green and a darker magenta, which otherwise look alike.</summary>
    public static TerminalPalette Deuteranopia { get; } = new(
        "#1B1E24", "#E05F39", "#71D1AA", "#F0E442", "#3976FE", "#AE6891", "#57CFF8", "#C8CCD4",
        "#7A8292", "#EC753F", "#79DFC9", "#FFFA90", "#88A0FF", "#C283B3", "#6BE7FF", "#FFFFFF");

    /// <summary>For tritanopia: a redder red, a darker green and a violet blue, apart from cyan and magenta.</summary>
    public static TerminalPalette Tritanopia { get; } = new(
        "#1B1E24", "#EE441F", "#2A937D", "#F3E946", "#9574FF", "#D28DBA", "#57D9FF", "#C8CCD4",
        "#7A8292", "#F9671F", "#36A189", "#FAF39A", "#ACABFF", "#EEA4CE", "#6EE7FF", "#FFFFFF");

    /// <summary>The colours for <paramref name="scheme"/>; colour-blind friendly is tuned to <paramref name="vision"/> when there is one.</summary>
    public static TerminalPalette For(TerminalColourScheme scheme, ColourVision vision = ColourVision.None) =>
        scheme != TerminalColourScheme.ColourBlind ? Standard
        : vision switch
        {
            ColourVision.Protanopia => Protanopia,
            ColourVision.Deuteranopia => Deuteranopia,
            ColourVision.Tritanopia => Tritanopia,
            _ => ColourBlind,
        };
}
