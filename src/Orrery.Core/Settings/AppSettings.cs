using System.Text.Json;
using System.Text.Json.Serialization;
using Orrery.Core.Network;
using Orrery.Core.Storage;
using Orrery.Core.Updates;

namespace Orrery.Core.Settings;

/// <summary>Where saved credentials live. Only installed mode on Windows offers a choice today.</summary>
public enum CredentialStoreKind
{
    /// <summary>The OS keychain: Windows Credential Manager, later macOS Keychain and Secret Service.</summary>
    OsKeychain,

    /// <summary><c>vault.frv</c> in the data folder, opened with a master password.</summary>
    VaultFile,
}

/// <summary>What Orrery does when it finds a newer release.</summary>
public enum UpdateAction
{
    /// <summary>Says so, and downloads nothing until asked.</summary>
    Ask,
    /// <summary>Downloads and checks it in the background, then says it's ready.</summary>
    Download,
}

/// <summary>How one of the window's side panes shows beside the sessions.</summary>
public enum PaneMode
{
    /// <summary>Always beside the sessions.</summary>
    Expanded,

    /// <summary>Only its icons; it opens over the sessions when one is clicked, so a remote desktop keeps its size.</summary>
    Collapsed,

    /// <summary>No longer offered: it moved the panes as the first tab opened and the last closed. Read as Collapsed.</summary>
    Auto,

    /// <summary>Nothing on screen; its shortcut or the window's edge opens it over the sessions.</summary>
    Hidden,
}

/// <summary>The 16 colors terminals draw with.</summary>
public enum TerminalColorScheme
{
    Standard,

    /// <summary>Red and green kept apart for color blindness; tuned further for the <see cref="ColorVision"/> chosen.</summary>
    ColorBlind,
}

/// <summary>A kind of color blindness the app allows for, in its colors and by correcting remote desktops.</summary>
public enum ColorVision
{
    None,

    /// <summary>Red-weak: reds look dark and close to greens.</summary>
    Protanopia,

    /// <summary>Green-weak, the most common: reds, greens and yellows run together.</summary>
    Deuteranopia,

    /// <summary>Blue-yellow: blues look like greens, and yellows like pinks or grays.</summary>
    Tritanopia,
}

/// <summary>The colors that carry meaning in the interface: states, success, warnings, errors.</summary>
public enum InterfaceColorScheme
{
    Standard,

    /// <summary>Brighter colors that are easier to tell apart for most kinds of color blindness.</summary>
    Clearer,

    /// <summary>Okabe and Ito's color-blind safe colors.</summary>
    ColorBlind,
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

    /// <summary>
    /// Open web connections and management pages in a tab in Orrery (null is on) rather than the default browser.
    /// A connection or folder can choose for itself; see InheritableSettings.WebPagesInOrrery.
    /// </summary>
    public bool? WebPagesInOrrery { get; init; }

    [JsonIgnore]
    public bool OpensWebPagesInOrrery => WebPagesInOrrery ?? true;

    /// <summary>Before <see cref="SidePanelMode"/>: the side panel was showing. Null is open. Still read when that's unset.</summary>
    public bool? SidePanelOpen { get; init; }

    /// <summary>Before <see cref="InspectorMode"/>: the right-hand pane was showing. Null is open. Still read when that's unset.</summary>
    public bool? InspectorOpen { get; init; }

    /// <summary>How the side panel (connections, credentials, recent) shows. Null follows <see cref="SidePanelOpen"/>.</summary>
    public PaneMode? SidePanelMode { get; init; }

    /// <summary>How the right-hand pane (connection settings, credential editor) shows. Null follows <see cref="InspectorOpen"/>.</summary>
    public PaneMode? InspectorMode { get; init; }

    /// <summary>The side panel's mode: a closed panel from before modes kept its rail, which Collapsed is.</summary>
    [JsonIgnore]
    public PaneMode SidePanel => Offered(SidePanelMode ?? (SidePanelOpen == false ? PaneMode.Collapsed : PaneMode.Expanded));

    /// <summary>The right-hand pane's mode: a closed pane from before modes left nothing on screen, which Hidden is.</summary>
    [JsonIgnore]
    public PaneMode Inspector => Offered(InspectorMode ?? (InspectorOpen == false ? PaneMode.Hidden : PaneMode.Expanded));

    static PaneMode Offered(PaneMode mode) => mode == PaneMode.Auto ? PaneMode.Collapsed : mode;

    /// <summary>The side panel's width as last dragged, in device-independent units. Null is the default.</summary>
    public double? SidePanelWidth { get; init; }

    /// <summary>The right-hand pane's width as last dragged. Null is the default.</summary>
    public double? InspectorWidth { get; init; }

    /// <summary>
    /// Check from time to time whether the connections on screen are up, the way <see cref="HostStatusMethod"/> says.
    /// Null is on. Off sends nothing and shows no dots.
    /// </summary>
    public bool? HostStatusChecks { get; init; }

    [JsonIgnore]
    public bool ChecksHostStatus => HostStatusChecks ?? true;

    /// <summary>
    /// How hosts are checked: a TCP connection to each one's host and port, or a ping to the host. Null is
    /// <see cref="HostCheckMethod.Tcp"/>.
    /// </summary>
    public HostCheckMethod? HostStatusMethod { get; init; }

    [JsonIgnore]
    public HostCheckMethod HostCheckMethod => HostStatusMethod ?? HostCheckMethod.Tcp;

    /// <summary>
    /// Whether to reopen last time's sessions at start-up. Null is <see cref="RestoreSessionsMode.Ask"/>.
    /// <see cref="RestoreSessionsMode.Never"/> also stops the open tabs being written down at all.
    /// </summary>
    public RestoreSessionsMode? RestoreSessions { get; init; }

    [JsonIgnore]
    public RestoreSessionsMode RestoreMode => RestoreSessions ?? RestoreSessionsMode.Ask;

    /// <summary>
    /// Keep the Recent list and each connection's last connected and last failed times (<c>recent.json</c> and
    /// <c>history.json</c>). Null is on. Turning it off deletes both files, and nothing is recorded until it's on again.
    /// </summary>
    public bool? RememberRecentConnections { get; init; }

    [JsonIgnore]
    public bool RemembersRecentConnections => RememberRecentConnections ?? true;

    /// <summary>
    /// Double-click, Enter or Connect on a connection that's already open: true opens another session, false goes to the
    /// open one. Null is false. Ctrl+double-click, a middle-click and "Open new session" always open another.
    /// </summary>
    public bool? NewSessionOnOpen { get; init; }

    [JsonIgnore]
    public bool OpensNewSession => NewSessionOnOpen ?? false;

    /// <summary>Terminal text size in points. Null is <see cref="DefaultTerminalFontSize"/>.</summary>
    public double? TerminalFontSize { get; init; }

    public const double DefaultTerminalFontSize = 14;

    /// <summary>The terminal's colors. Null is <see cref="TerminalColorScheme.Standard"/>.</summary>
    public TerminalColorScheme? TerminalColors { get; init; }

    /// <summary>The interface colors. Null is <see cref="InterfaceColorScheme.Standard"/>, unless <see cref="ColorBlindColors"/> says otherwise.</summary>
    public InterfaceColorScheme? InterfaceColors { get; init; }

    /// <summary>
    /// The old on/off setting, read from existing files and never written back: <see cref="AppSettingsStore"/> turns
    /// true into <see cref="InterfaceColorScheme.ColorBlind"/> when it loads or saves. Its key keeps the British
    /// spelling the builds that wrote it used.
    /// </summary>
    [JsonPropertyName("colourBlindColours")]
    public bool? ColorBlindColors { get; init; }

    /// <summary>The interface colors in use, counting the old setting.</summary>
    [JsonIgnore]
    public InterfaceColorScheme InterfaceColorScheme =>
        InterfaceColors ?? (ColorBlindColors == true ? InterfaceColorScheme.ColorBlind : InterfaceColorScheme.Standard);

    /// <summary>Moves the old color-blind setting into <see cref="InterfaceColors"/>.</summary>
    public AppSettings Migrated() =>
        ColorBlindColors is null
            ? this
            : this with { InterfaceColors = InterfaceColors ?? (ColorBlindColors == true ? InterfaceColorScheme.ColorBlind : null), ColorBlindColors = null };

    /// <summary>
    /// The user's color vision. Null is none. Choosing one in Settings also picks the color-blind interface and
    /// terminal colors, which stay separate settings the user can change back.
    /// </summary>
    public ColorVision? ColorVision { get; init; }

    [JsonIgnore]
    public ColorVision Vision => ColorVision ?? default;

    /// <summary>How strongly remote desktops are corrected for <see cref="ColorVision"/>, 0 to 100 percent. Null is <see cref="DefaultColorCorrectionStrength"/>.</summary>
    public int? ColorCorrectionStrength { get; init; }

    public const int DefaultColorCorrectionStrength = 100;

    /// <summary>The correction strength in use, 0 to 100; a hand-edited value out of range is brought into it.</summary>
    [JsonIgnore]
    public int CorrectionStrength => Math.Clamp(ColorCorrectionStrength ?? DefaultColorCorrectionStrength, 0, 100);

    // Builds before the switch to American spelling wrote these keys. They're read into the settings above, which
    // win when a file has both, and never written: the getters return null, which isn't written.
    [JsonInclude, JsonPropertyName("terminalColours")]
    TerminalColorScheme? BritishTerminalColors { get => null; init => TerminalColors ??= value; }

    [JsonInclude, JsonPropertyName("interfaceColours")]
    InterfaceColorScheme? BritishInterfaceColors { get => null; init => InterfaceColors ??= value; }

    [JsonInclude, JsonPropertyName("colourVision")]
    ColorVision? BritishColorVision { get => null; init => ColorVision ??= value; }

    [JsonInclude, JsonPropertyName("colourCorrectionStrength")]
    int? BritishColorCorrectionStrength { get => null; init => ColorCorrectionStrength ??= value; }

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

    /// <summary>The Settings page last shown, by name. Null, or a name no longer there, is the first page.</summary>
    public string? SettingsPage { get; init; }

    // ---- Recordings (docs/design-session-recording.md §4.2) ----

    /// <summary>Days a recording is kept; 0 keeps them until deleted. Null is <see cref="DefaultRecordingKeepDays"/>.</summary>
    public int? RecordingKeepDays { get; init; }

    public const int DefaultRecordingKeepDays = 30;

    /// <summary>Gigabytes all recordings together may use. Null is <see cref="DefaultRecordingTotalGB"/>.</summary>
    public int? RecordingTotalGB { get; init; }

    public const int DefaultRecordingTotalGB = 5;

    /// <summary>Gigabytes one recording may reach before it stops. Null is <see cref="DefaultRecordingFileGB"/>.</summary>
    public int? RecordingFileGB { get; init; }

    public const int DefaultRecordingFileGB = 1;

    /// <summary>
    /// The most frames a second a remote desktop recording takes: one of <see cref="RecordingFrameRates"/>. A frame is
    /// taken only when the screen changed, so a desktop that sends fewer gets fewer. Null is <see cref="DefaultRecordingFrameRate"/>.
    /// </summary>
    public int? RecordingFrameRate { get; init; }

    public const int DefaultRecordingFrameRate = 5;

    /// <summary>
    /// The frame rates on offer. Windows sends at most about 30 frames a second unless its server is set for 60, so 60
    /// helps only there; elsewhere the recording simply has as many frames as the desktop sends.
    /// </summary>
    public static readonly IReadOnlyList<int> RecordingFrameRates = [1, 2, 5, 10, 15, 30, 60];

    /// <summary>Put the connection's name in a recording's file name. Null is on.</summary>
    public bool? RecordingNamesInFiles { get; init; }

    /// <summary>Record the sound of remote desktops that play it here (RDP). Null is on.</summary>
    public bool? RecordingSound { get; init; }

    /// <summary>The ffmpeg executable MP4 export runs, as the user chose it. Null offers only the built-in AVI export.</summary>
    public string? FfmpegPath { get; init; }

    /// <summary>
    /// The recordings public key (X25519, base64) recordings are sealed to; its private half is in the credential
    /// store. Null until the first recording; recovered from the private key if it's lost.
    /// </summary>
    public string? RecordingPublicKey { get; init; }

    /// <summary>The note about what recordings hold has been shown and accepted.</summary>
    public bool? RecordingNoteShown { get; init; }

    /// <summary>
    /// The first-launch setup has been gone through, or skipped with its defaults. Null on a data folder that already
    /// had settings before setup existed, which counts as done: only a folder with no settings at all gets it.
    /// </summary>
    public bool? SetupDone { get; init; }

    /// <summary>
    /// Ask GitHub from time to time whether there's a newer release. Null is on. Off sends nothing; checking by hand
    /// still works.
    /// </summary>
    public bool? UpdateChecks { get; init; }

    [JsonIgnore]
    public bool ChecksForUpdates => UpdateChecks ?? true;

    /// <summary>What happens when a newer release is found. Null is <see cref="UpdateAction.Ask"/>.</summary>
    public UpdateAction? WhenUpdateFound { get; init; }

    [JsonIgnore]
    public UpdateAction UpdateAction => WhenUpdateFound ?? UpdateAction.Ask;

    /// <summary>
    /// Offer preview releases (GitHub's pre-releases) as well. Null follows the running Orrery: on when it's a preview
    /// itself, so a preview hears of the next one, off otherwise.
    /// </summary>
    public bool? UpdatePreviews { get; init; }

    [JsonIgnore]
    public bool OffersPreviewUpdates => OffersPreviewUpdatesTo(ReleaseVersion.Current);

    /// <summary>Whether previews are offered to a copy of this version.</summary>
    public bool OffersPreviewUpdatesTo(ReleaseVersion current) => UpdatePreviews ?? current.IsPrerelease;

    /// <summary>A release the user chose to skip, by version: it isn't offered again, a later one is.</summary>
    public string? SkippedUpdateVersion { get; init; }

    /// <summary>When the last automatic check for updates ran, so the next waits <see cref="UpdateCheckInterval"/>.</summary>
    public DateTimeOffset? LastUpdateCheck { get; init; }

    public const int DefaultUpdateCheckMinutes = 60;

    /// <summary>Minutes between automatic update checks while Orrery stays open. Null is <see cref="DefaultUpdateCheckMinutes"/>.</summary>
    public int? UpdateCheckMinutes { get; init; }

    [JsonIgnore]
    public TimeSpan UpdateCheckInterval => TimeSpan.FromMinutes(UpdateCheckMinutes is > 0 and var minutes ? minutes : DefaultUpdateCheckMinutes);

    [JsonIgnore]
    public TimeSpan? RecordingKeepFor => (RecordingKeepDays ?? DefaultRecordingKeepDays) is > 0 and var days ? TimeSpan.FromDays(days) : null;

    [JsonIgnore]
    public long RecordingTotalBytes => Math.Max(1, RecordingTotalGB ?? DefaultRecordingTotalGB) * Gigabyte;

    [JsonIgnore]
    public long RecordingFileBytes => Math.Max(1, RecordingFileGB ?? DefaultRecordingFileGB) * Gigabyte;

    /// <summary>The frame rate in use; a hand-edited value is brought to the nearest allowed one.</summary>
    [JsonIgnore]
    public int RecordingFramesPerSecond
    {
        get
        {
            int wanted = RecordingFrameRate ?? DefaultRecordingFrameRate;
            return RecordingFrameRates.MinBy(rate => (Math.Abs(rate - wanted), -rate));
        }
    }

    [JsonIgnore]
    public bool RecordsSound => RecordingSound ?? true;

    [JsonIgnore]
    public bool PutsNamesInRecordingFiles => RecordingNamesInFiles ?? true;

    const long Gigabyte = 1024L * 1024 * 1024;

    /// <summary>Null for "never", which the vault takes as no timer.</summary>
    [JsonIgnore]
    public TimeSpan? AutoLockAfter => (AutoLockMinutes ?? DefaultAutoLockMinutes) is > 0 and var minutes ? TimeSpan.FromMinutes(minutes) : null;

    public const int DefaultLogDays = 14;
    public static readonly TimeSpan DefaultKeepLogsFor = TimeSpan.FromDays(DefaultLogDays);

    /// <summary>Days log files are kept. Null is <see cref="DefaultLogDays"/>.</summary>
    public int? LogDays { get; init; }

    [JsonIgnore]
    public TimeSpan KeepLogsFor => TimeSpan.FromDays(LogDays is > 0 and var days ? days : DefaultLogDays);
}

public sealed class AppSettingsStore(string path)
{
    public const string FileName = "settings.json";

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new BritishSpellingEnumConverter() },
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
