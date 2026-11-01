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

    public static TerminalPalette For(TerminalColourScheme scheme) =>
        scheme == TerminalColourScheme.ColourBlind ? ColourBlind : Standard;
}
