namespace Orrery.Core.Sessions;

public enum SessionState
{
    Connecting,
    Connected,
    Disconnected,
    Failed,
}

/// <summary>Where a session connects to. Secrets are passed separately and never stored here.</summary>
public sealed record ConnectionTarget(string Host, int Port, string? Username, string? Domain);

public sealed class SessionStateChangedEventArgs(SessionState state, string? message) : EventArgs
{
    public SessionState State { get; } = state;
    public string? Message { get; } = message;
}

/// <summary>A live connection to a remote machine, whatever the protocol.</summary>
public interface IConnectionSession : IAsyncDisposable
{
    ConnectionTarget Target { get; }
    SessionState State { get; }
    event EventHandler<SessionStateChangedEventArgs>? StateChanged;
    Task DisconnectAsync();
}
