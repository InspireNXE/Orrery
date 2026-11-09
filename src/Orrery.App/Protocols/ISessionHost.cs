using Avalonia.Controls;
using Orrery.App.Sessions;
using Orrery.Core.Connections;
using Orrery.Core.Credentials;
using Orrery.Core.Security;
using Orrery.Core.Sessions;
using Orrery.Protocols.Serial;
using Orrery.Protocols.Ssh;

namespace Orrery.App.Protocols;

/// <summary>
/// What the main window gives a protocol to start its sessions with: a tab, a tunnel, the views it draws in, and
/// what its servers are trusted by. See docs/design-protocol-registry.md, §4.3.
/// </summary>
interface ISessionHost
{
    /// <summary>
    /// Opens a session in a new tab. When it should reconnect by itself, <paramref name="create"/> is kept so it
    /// can be opened again the same way, login included, without asking anything.
    /// </summary>
    void Open(SessionOrigin origin, Func<(Control View, IConnectionSession Session)> create);

    /// <summary>
    /// A new tunnel through <paramref name="jumps"/>, or none to connect directly. Made inside each session's
    /// create function, so reconnecting opens a fresh one; the session disposes it.
    /// </summary>
    SshTunnel? Tunnel(IReadOnlyList<SshHop>? jumps);

    /// <summary>A remote desktop view with the color correction from Settings, unless its connection turns that off.</summary>
    RemoteFrameView NewDesktopView(SessionOrigin origin, IFrameBufferSession session, bool zoom, bool followsViewSize);

    /// <summary>A terminal at the saved text size and colors.</summary>
    TerminalView NewTerminalView(ITerminalSession session);

    /// <summary>Settings' "Match display scaling", for a connection that doesn't choose.</summary>
    bool EnlargesByDefault { get; }

    /// <summary>The starting desktop size, in remote pixels, filling the session area.</summary>
    (int Width, int Height) DesktopSizeForView(bool zoom);

    /// <summary>This display's scaling, which a desktop that follows the view is told about.</summary>
    double DisplayScale { get; }

    ServerCertificateValidator CertificateValidator { get; }

    HostKeyValidator HostKeyValidator { get; }

    /// <summary>A saved connection or folder by its id, read again each time a session (re)starts.</summary>
    ConnectionNode? Find(Guid id);

    /// <summary>Closes the tab of <paramref name="session"/>, ending it.</summary>
    Task CloseTabAsync(IConnectionSession session);

    /// <summary>For a protocol's own messages and prompts.</summary>
    Window Owner { get; }

    /// <summary>A session that may use the credential store has started; the vault button follows.</summary>
    void LoginsChanged();

    /// <summary>A shell on this computer in a new tab: <paramref name="shellId"/>, or the one chosen in Settings.</summary>
    void OpenLocalTerminal(string? shellId, bool elevated, ConnectionEntry? entry);

    /// <summary>A web page in a tab, or in the browser if the connection or Settings says so.</summary>
    Task OpenWebPageAsync(string title, Guid? entryId, Protocol protocol, string host, int port, bool behindJumpHost);
}

/// <summary>
/// How a session starts, as its connection's settings say; a host typed into quick connect gets these defaults,
/// not the defaults for all connections.
/// </summary>
sealed record StartOptions
{
    public bool ShareClipboard { get; init; } = true;

    public bool AllowLegacySsh { get; init; }

    /// <summary>A fixed remote desktop size; null fits the session area and follows it.</summary>
    public (int Width, int Height)? DesktopSize { get; init; }

    /// <summary>The connection's own "Match display scaling"; null uses Settings'.</summary>
    public bool? Enlarge { get; init; }

    public DisplayQuality Quality { get; init; } = DisplayQuality.Balanced;

    /// <summary>VNC: open watching only.</summary>
    public bool ViewOnly { get; init; }

    public DesktopScaling Scaling { get; init; } = DesktopScaling.Fit;

    public SerialLine Line { get; init; } = SerialLine.Default;

    public static StartOptions QuickConnect { get; } = new();

    public static StartOptions From(ResolvedSettings resolved)
    {
        // Checked when saved; a hand-edited file that doesn't parse just fits the window.
        DesktopResolution.TryParse(resolved.DesktopSize.Value, out var desktopSize, out _);
        return new StartOptions
        {
            ShareClipboard = resolved.ShareClipboard.Value,
            AllowLegacySsh = resolved.AllowLegacySsh.Value,
            DesktopSize = desktopSize,
            Enlarge = resolved.EnlargeRemoteDesktop.Value,
            Quality = resolved.DisplayQuality.Value,
            ViewOnly = resolved.ViewOnly.Value,
            Scaling = resolved.Scaling.Value,
            Line = SerialLine.From(resolved),
        };
    }
}

/// <summary>What a session logs in with.</summary>
/// <param name="Password">Typed at the prompt or from a saved credential; null for keys only or no login.</param>
/// <param name="Saved">The saved credential it came from, if any: an SSH-key one logs in with its key.</param>
/// <param name="KeyFile">SSH: the connection's key file, tried before the password.</param>
/// <param name="KeysOnly">SSH: the key file and the agent's keys, no password; if they're refused,
/// <paramref name="WhenKeysRefused"/> asks for a login the usual way.</param>
sealed record SessionLogin(string? Password = null, Credential? Saved = null, KeyFileLogin? KeyFile = null, bool KeysOnly = false, bool UseAgent = false,
    Func<Task>? WhenKeysRefused = null)
{
    public static SessionLogin None { get; } = new();

    public override string ToString() => "SessionLogin";
}

/// <summary>A connection's key file and the passphrase it needs, if any, which is held only for this session.</summary>
sealed record KeyFileLogin(string Path, string? Passphrase)
{
    public override string ToString() => "KeyFileLogin";
}

/// <summary>What an SSH session goes through and opens besides its shell, and how often it sends keep-alives (null: the default).</summary>
sealed record SshRoute(IReadOnlyList<SshHop> Jumps, IReadOnlyList<PortForward> Forwards, TimeSpan? KeepAlive = null)
{
    public static SshRoute Direct { get; } = new([], []);
}

/// <summary>What to open without a login: a saved connection, or a host typed into quick connect.</summary>
/// <param name="Title">The connection's name, or the host typed.</param>
/// <param name="Entry">The saved connection; null for quick connect.</param>
/// <param name="BehindJumpHost">A saved connection set to go through a jump host.</param>
sealed record OpenRequest(Protocol Protocol, string Title, ConnectionEntry? Entry, string Host, int Port, bool BehindJumpHost = false);

/// <summary>A session to start: the tab it opens in, where it goes, and how.</summary>
sealed record StartRequest(SessionOrigin Origin, ConnectionTarget Target, SessionLogin Login, StartOptions Options, SshRoute Route);
