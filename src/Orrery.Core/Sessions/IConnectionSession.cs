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

/// <param name="error">For a failure, what was thrown, so <see cref="ConnectionFailure.Classify"/> can tell what happened.</param>
public sealed class SessionStateChangedEventArgs(SessionState state, string? message, Exception? error = null) : EventArgs
{
    public SessionState State { get; } = state;
    public string? Message { get; } = message;
    public Exception? Error { get; } = error;
}

/// <summary>A live connection to a remote machine, whatever the protocol.</summary>
public interface IConnectionSession : IAsyncDisposable
{
    ConnectionTarget Target { get; }
    SessionState State { get; }
    event EventHandler<SessionStateChangedEventArgs>? StateChanged;
    Task DisconnectAsync();
}
