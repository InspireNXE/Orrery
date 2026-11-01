using Avalonia.Controls;
using Avalonia.Media;

namespace Orrery.App;

/// <summary>
/// The colours that carry meaning: session states, success, warnings, errors and the accent. Each has a word or a
/// shape beside it too, so none of them is the only sign of anything.
/// </summary>
sealed record UiColours(
    Color StateUp,
    Color StateConnecting,
    Color StateDown,
    Color StateEnded,
    Color Success,
    Color Warning,
    Color Danger,
    Color Accent);

/// <summary>
/// The app's meaning-carrying brushes, shared by every window and published as resources (see
/// <see cref="ResourceKeys"/>). Switching sets change the brushes' colours in place, so whatever uses them follows
/// at once, without a restart.
/// </summary>
static class Palette
{
    /// <summary>The palette from the mockup.</summary>
    public static readonly UiColours Standard = new(
        StateUp: Color.Parse("#5FD38D"),
        StateConnecting: Color.Parse("#E0B45A"),
        StateDown: Color.Parse("#FF9AA2"),
        StateEnded: Color.Parse("#5B6372"),
        Success: Color.Parse("#5FD38D"),
        Warning: Color.Parse("#F3B672"),
        Danger: Color.Parse("#FF9AA2"),
        Accent: Color.Parse("#5B9DFF"));

    /// <summary>
    /// Okabe and Ito's colours, lightened to read on the dark panels: sky blue for up and success, yellow for
    /// connecting and warnings, vermilion for down and errors. Blue, yellow and vermilion stay apart for red-green
    /// and blue-yellow colour blindness alike.
    /// </summary>
    public static readonly UiColours ColourBlind = new(
        StateUp: Color.Parse("#56B4E9"),
        StateConnecting: Color.Parse("#F0E442"),
        StateDown: Color.Parse("#E0603A"),
        StateEnded: Color.Parse("#7D7D7D"),
        Success: Color.Parse("#56B4E9"),
        Warning: Color.Parse("#F0E442"),
        Danger: Color.Parse("#E0603A"),
        Accent: Color.Parse("#56B4E9"));

    public static SolidColorBrush StateUp { get; } = new(Standard.StateUp);
    public static SolidColorBrush StateConnecting { get; } = new(Standard.StateConnecting);
    public static SolidColorBrush StateDown { get; } = new(Standard.StateDown);
    public static SolidColorBrush StateEnded { get; } = new(Standard.StateEnded);
    public static SolidColorBrush Success { get; } = new(Standard.Success);
    public static SolidColorBrush Warning { get; } = new(Standard.Warning);
    public static SolidColorBrush Danger { get; } = new(Standard.Danger);
    public static SolidColorBrush Accent { get; } = new(Standard.Accent);

    /// <summary>The set in use.</summary>
    public static UiColours Current { get; private set; } = Standard;

    public static bool IsColourBlind => Current == ColourBlind;

    /// <summary>Raised on the UI thread after the brushes change.</summary>
    public static event EventHandler? Changed;

    /// <summary>Resource keys and the brush behind each. <c>OrreryConnected</c> is the older name for up.</summary>
    public static IReadOnlyList<(string Key, SolidColorBrush Brush)> ResourceKeys { get; } =
    [
        ("OrreryStateUp", StateUp),
        ("OrreryStateConnecting", StateConnecting),
        ("OrreryStateDown", StateDown),
        ("OrreryStateEnded", StateEnded),
        ("OrrerySuccess", Success),
        ("OrreryWarning", Warning),
        ("OrreryDanger", Danger),
        ("OrreryAccent", Accent),
        ("OrreryConnected", StateUp),
    ];

    /// <summary>Puts the brushes in <paramref name="resources"/>, over the defaults App.axaml declares.</summary>
    public static void AddTo(IResourceDictionary resources)
    {
        foreach (var (key, brush) in ResourceKeys)
        {
            resources[key] = brush;
        }
    }

    /// <summary>Switches between <see cref="Standard"/> and <see cref="ColourBlind"/>.</summary>
    public static void Use(bool colourBlind)
    {
        var colours = colourBlind ? ColourBlind : Standard;
        if (colours == Current)
        {
            return;
        }

        Current = colours;
        StateUp.Color = colours.StateUp;
        StateConnecting.Color = colours.StateConnecting;
        StateDown.Color = colours.StateDown;
        StateEnded.Color = colours.StateEnded;
        Success.Color = colours.Success;
        Warning.Color = colours.Warning;
        Danger.Color = colours.Danger;
        Accent.Color = colours.Accent;
        Changed?.Invoke(null, EventArgs.Empty);
    }
}
