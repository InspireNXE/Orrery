namespace Orrery.Core.Sessions;

/// <summary>
/// A session that exchanges terminal bytes (SSH, Telnet): output arrives through <see cref="DataReceived"/>,
/// keystrokes go out through <see cref="Send"/>. Events are raised on a background thread.
/// </summary>
public interface ITerminalSession : IConnectionSession
{
    event Action<byte[]>? DataReceived;

    void Send(ReadOnlySpan<byte> data);

    /// <summary>Tells the remote side the terminal's size in character cells.</summary>
    void Resize(int columns, int rows);
}
