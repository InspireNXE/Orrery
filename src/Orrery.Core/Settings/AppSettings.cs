using System.Text.Json;
using System.Text.Json.Serialization;
using Orrery.Core.Storage;

namespace Orrery.Core.Settings;

/// <summary>Where saved credentials live. Only installed mode on Windows offers a choice today.</summary>
public enum CredentialStoreKind
{
    /// <summary>The OS keychain: Windows Credential Manager, later macOS Keychain and Secret Service.</summary>
    OsKeychain,

    /// <summary><c>vault.frv</c> in the data folder, opened with a master password.</summary>
    VaultFile,
}

/// <summary>The 16 colours terminals draw with.</summary>
public enum TerminalColourScheme
{
    Standard,

    /// <summary>Red and green kept apart for colour blindness; tuned further for the <see cref="ColourVision"/> chosen.</summary>
    ColourBlind,
}

/// <summary>A kind of colour blindness the app allows for, in its colours and by correcting remote desktops.</summary>
public enum ColourVision
{
    None,

    /// <summary>Red-weak: reds look dark and close to greens.</summary>
    Protanopia,

    /// <summary>Green-weak, the most common: reds, greens and yellows run together.</summary>
    Deuteranopia,

    /// <summary>Blue-yellow: blues look like greens, and yellows like pinks or greys.</summary>
    Tritanopia,
}

/// <summary>The colours that carry meaning in the interface: states, success, warnings, errors.</summary>
public enum InterfaceColourScheme
{
    Standard,

    /// <summary>Brighter colours that are easier to tell apart for most kinds of colour blindness.</summary>
    Clearer,

    /// <summary>Okabe and Ito's colour-blind safe colours.</summary>
    ColourBlind,
}

/// <summary>
/// App preferences, kept in <c>settings.json</c> in the data folder. A null value means "use the default",
/// so defaults can change in later versions without rewriting everyone's file.
/// </summary>
public sealed record AppSettings
{
    public const int CurrentSchemaVersion = 1;
    public const int DefaultAutoLockMinutes = 30;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Null picks the OS keychain where there is one and the vault file elsewhere.</summary>
    public CredentialStoreKind? CredentialStore { get; init; }

    /// <summary>Minutes of vault inactivity before it locks; 0 means never. Null is <see cref="DefaultAutoLockMinutes"/>.</summary>
    public int? AutoLockMinutes { get; init; }

    /// <summary>
    /// Enlarge remote desktops to match this display's scaling (null is on). Off shows them pixel for pixel,
    /// sharp but with 100% sized text, since the server isn't told the display scale.
    /// </summary>
    public bool? RemoteDesktopZoom { get; init; }

    [JsonIgnore]
    public bool ZoomRemoteDesktops => RemoteDesktopZoom ?? true;

    /// <summary>The side panel (connections, credentials, recent) is showing. Null is open.</summary>
    public bool? SidePanelOpen { get; init; }

    /// <summary>The right-hand pane (connection settings, credential editor) is showing. Null is open.</summary>
    public bool? InspectorOpen { get; init; }

    /// <summary>
    /// Check from time to time whether the connections on screen are up, by opening a TCP connection to each one's
    /// host and port. Null is on. Off sends nothing and shows no dots.
    /// </summary>
    public bool? HostStatusChecks { get; init; }

    [JsonIgnore]
    public bool ChecksHostStatus => HostStatusChecks ?? true;

    /// <summary>Whether to reopen last time's sessions at start-up. Null is <see cref="RestoreSessionsMode.Ask"/>.</summary>
    public RestoreSessionsMode? RestoreSessions { get; init; }

    [JsonIgnore]
    public RestoreSessionsMode RestoreMode => RestoreSessions ?? RestoreSessionsMode.Ask;

    /// <summary>Terminal text size in points. Null is <see cref="DefaultTerminalFontSize"/>.</summary>
    public double? TerminalFontSize { get; init; }

    public const double DefaultTerminalFontSize = 14;

    /// <summary>The terminal's colours. Null is <see cref="TerminalColourScheme.Standard"/>.</summary>
    public TerminalColourScheme? TerminalColours { get; init; }

    /// <summary>The interface colours. Null is <see cref="InterfaceColourScheme.Standard"/>, unless <see cref="ColourBlindColours"/> says otherwise.</summary>
    public InterfaceColourScheme? InterfaceColours { get; init; }

    /// <summary>
    /// The old on/off setting, read from existing files and never written back: <see cref="AppSettingsStore"/> turns
    /// true into <see cref="InterfaceColourScheme.ColourBlind"/> when it loads or saves.
    /// </summary>
    public bool? ColourBlindColours { get; init; }

    /// <summary>The interface colours in use, counting the old setting.</summary>
    [JsonIgnore]
    public InterfaceColourScheme InterfaceColourScheme =>
        InterfaceColours ?? (ColourBlindColours == true ? InterfaceColourScheme.ColourBlind : InterfaceColourScheme.Standard);

    /// <summary>Moves the old colour-blind setting into <see cref="InterfaceColours"/>.</summary>
    public AppSettings Migrated() =>
        ColourBlindColours is null
            ? this
            : this with { InterfaceColours = InterfaceColours ?? (ColourBlindColours == true ? InterfaceColourScheme.ColourBlind : null), ColourBlindColours = null };

    /// <summary>
    /// The user's colour vision. Null is none. Choosing one in Settings also picks the colour-blind interface and
    /// terminal colours, which stay separate settings the user can change back.
    /// </summary>
    public ColourVision? ColourVision { get; init; }

    [JsonIgnore]
    public ColourVision Vision => ColourVision ?? default;

    /// <summary>How strongly remote desktops are corrected for <see cref="ColourVision"/>, 0 to 100 percent. Null is <see cref="DefaultColourCorrectionStrength"/>.</summary>
    public int? ColourCorrectionStrength { get; init; }

    public const int DefaultColourCorrectionStrength = 100;

    /// <summary>The correction strength in use, 0 to 100; a hand-edited value out of range is brought into it.</summary>
    [JsonIgnore]
    public int CorrectionStrength => Math.Clamp(ColourCorrectionStrength ?? DefaultColourCorrectionStrength, 0, 100);

    /// <summary>
    /// The key that turns full screen on and off, e.g. "Ctrl+Alt+End". Null is the default (Ctrl+Alt+Enter,
    /// or Ctrl+Cmd+F on macOS). The app checks it before use and falls back to the default.
    /// </summary>
    public string? FullScreenShortcut { get; init; }

    /// <summary>
    /// Read new terminal output out through the screen reader. Null is automatic: on while a screen reader is
    /// running when Orrery starts, otherwise off.
    /// </summary>
    public bool? TerminalAnnouncements { get; init; }

    /// <summary>
    /// The shell a local terminal runs, by id ("pwsh", "cmd", "wsl:Ubuntu"). Null, or one no longer installed, is
    /// PowerShell 7 if there is one, else Windows PowerShell; the login shell on Linux and macOS.
    /// </summary>
    public string? LocalShell { get; init; }

    /// <summary>Null for "never", which the vault takes as no timer.</summary>
    [JsonIgnore]
    public TimeSpan? AutoLockAfter => (AutoLockMinutes ?? DefaultAutoLockMinutes) is > 0 and var minutes ? TimeSpan.FromMinutes(minutes) : null;
}

public sealed class AppSettingsStore(string path)
{
    public const string FileName = "settings.json";

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static AppSettingsStore In(DataLocation location) => new(location.FileIn(FileName));

    /// <summary>
    /// Missing or unreadable settings fall back to defaults: losing a preference is better than refusing to start.
    /// </summary>
    public AppSettings Load()
    {
        try
        {
            return File.Exists(path) ? (JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions) ?? new()).Migrated() : new();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new();
        }
    }

    public void Save(AppSettings settings) =>
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(settings.Migrated() with { SchemaVersion = AppSettings.CurrentSchemaVersion }, JsonOptions));
}
