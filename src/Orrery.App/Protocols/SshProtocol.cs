using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Orrery.App.Connections;
using Orrery.App.Controls;
using Orrery.App.Sessions;
using Orrery.Core.Connections;
using Orrery.Core.Credentials;
using Orrery.Core.Sessions;
using Orrery.Protocols.Ssh;

namespace Orrery.App.Protocols;

sealed class SshProtocol() : ProtocolDescriptor(Protocol.Ssh)
{
    public override IBrush Background { get; } = Brush("#183526");

    public override IBrush Foreground { get; } = Brush("#7EE0A6");

    public override string Icon => Icons.Terminal;

    public override PropertiesSection CreateSection(PropertiesContext context) => new SshSection(context);

    public override bool OpensFiles => true;

    public override IReadOnlyList<PortForward> Forwards(ConnectionEntry entry, ResolvedSettings resolved) =>
        PortForward.WithSocks(entry.PortForwards, resolved.SocksProxy.Value);

    /// <summary>
    /// A terminal, or for <see cref="SessionOrigin.Files"/> a file browser (SFTP). SSH needs a username; a saved
    /// SSH-key credential logs in with its key instead of the password. With <see cref="SessionLogin.KeysOnly"/> it
    /// logs in with the key file, then the agent's keys, and no password: if the server refuses them all, the tab
    /// closes and <see cref="SessionLogin.WhenKeysRefused"/> asks for a login the usual way.
    /// </summary>
    public override async Task StartAsync(ISessionHost host, StartRequest request)
    {
        var (origin, target, login, options, route) = (request.Origin, request.Target, request.Login, request.Options, request.Route);
        if (login.KeysOnly)
        {
            host.Open(origin, () =>
            {
                var keys = new SshLogin(target.Username!, null, UseAgent: login.UseAgent, KeyFile: login.KeyFile?.Path, KeyFilePassphrase: login.KeyFile?.Passphrase);
                var (view, session, rejected) = NewView(host, origin, target, keys, options.AllowLegacySsh, route);
                session.StateChanged += (_, e) =>
                {
                    if (e.State == SessionState.Failed && rejected())
                    {
                        Dispatcher.UIThread.Post(async () =>
                        {
                            await host.CloseTabAsync(session);
                            await login.WhenKeysRefused!();
                        });
                    }
                };
                return (view, session);
            });
            return;
        }

        if (string.IsNullOrWhiteSpace(target.Username))
        {
            await Dialogs.ShowMessageAsync(host.Owner, "SSH needs a username", "Set a username on the connection or its saved credential, then connect again.");
            return;
        }

        var sshLogin = LoginFor(target.Username, login.Password, login.Saved, login.KeyFile);
        host.Open(origin, () =>
        {
            var (view, session, _) = NewView(host, origin, target, sshLogin, options.AllowLegacySsh, route);
            return (view, session);
        });
        host.LoginsChanged();
    }

    /// <summary>
    /// A terminal, or for <see cref="SessionOrigin.Files"/> a file browser (SFTP), on a new SSH connection.
    /// <c>Rejected</c> says whether the server turned the login down.
    /// </summary>
    static (Control View, IConnectionSession Session, Func<bool> Rejected) NewView(ISessionHost host, SessionOrigin origin, ConnectionTarget target, SshLogin login,
        bool allowLegacySsh, SshRoute route)
    {
        if (origin.Files)
        {
            var files = SftpSession.Start(target, login, host.HostKeyValidator, allowLegacySsh, route.Jumps, route.KeepAlive);
            return (new FileBrowserView(files), files, () => files.AuthenticationRejected);
        }

        var shell = SshSession.Start(target, login, host.HostKeyValidator, 80, 24, allowLegacySsh, route.Jumps, route.Forwards, route.KeepAlive);
        return (host.NewTerminalView(shell), shell, () => shell.AuthenticationRejected);
    }

    /// <summary>
    /// A saved SSH-key credential logs in with its key instead of the password. The connection's key file, if it has
    /// one, comes after that and before the password.
    /// </summary>
    internal static SshLogin LoginFor(string username, string? password, Credential? saved, KeyFileLogin? keyFile = null) =>
        saved?.Kind is CredentialKind.SshKey or CredentialKind.SshKeyWithPassphrase
            ? new SshLogin(username, null, saved.Secret, saved.Passphrase, KeyFile: keyFile?.Path, KeyFilePassphrase: keyFile?.Passphrase)
            : new SshLogin(username, password, KeyFile: keyFile?.Path, KeyFilePassphrase: keyFile?.Passphrase);
}
