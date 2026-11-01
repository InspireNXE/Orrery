using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Devolutions.IronRdp;
using Microsoft.Extensions.Logging;
using Orrery.Core.Security;
using Orrery.Core.Sessions;

namespace Orrery.Protocols.Rdp;

public sealed class CertificateRejectedException(string host)
    : SessionFailureException(FailureKind.CertificateNotTrusted, $"The certificate presented by {host} was not trusted.");

/// <summary>
/// The RDP connection sequence, following IronRDP's <c>Connection.Connect</c> (MIT/Apache-2.0) but with
/// our own TLS step: the server certificate is checked after the handshake and before CredSSP sends
/// any credentials. IronRDP's own helper accepts every certificate.
/// </summary>
static class RdpConnector
{
    /// <summary>How long the whole CredSSP exchange may take, including any Kerberos requests.</summary>
    static readonly TimeSpan CredsspTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The largest single CredSSP reply accepted.</summary>
    const int MaxCredsspReply = 64 * 1024;

    /// <summary>Fingers the input channel tracks at once, as Windows' own client offers; more are ignored until one lifts.</summary>
    internal const ushort MaxTouchContacts = 10;

    /// <summary>The name this computer gives the server, which shows it beside shared drives ("C on Orrery").</summary>
    internal const string ClientName = "Orrery";

    static readonly ILogger Logger = Orrery.Core.Diagnostics.Log.For("Rdp");

    /// <param name="dial">Where to open the connection: the target itself, or a tunnel's local end. TLS, the
    /// certificate check and CredSSP still use <paramref name="target"/>'s name.</param>
    /// <param name="redirectedFrom">The redirection that sent the connection here, whose routing token, session and
    /// password cookie go back to the server; null for the first connection.</param>
    /// <returns>The connection, and whether drives are shared, so the session drives their file operations.</returns>
    public static async Task<(ConnectionResult Result, Framed<SslStream> Framed, bool DrivesShared)> ConnectAsync(
        Config config, ConnectionTarget target, (string Host, int Port) dial, ServerCertificateValidator validator, CliprdrBackendFactory? clipboard,
        RdpOptions options, RdpRedirection? redirectedFrom, CancellationToken token)
    {
        var tcp = await OpenTcpAsync(dial.Host, dial.Port, token);
        var framed = new Framed<NetworkStream>(tcp.GetStream());

        var connector = ClientConnector.New(config, tcp.Client.LocalEndPoint!.ToString()!);
        connector.WithDynamicChannelDisplayControl();
        OfferGraphicsPipeline(connector);

        // Touch and pen (MS-RDPEI). A server without the channel never opens it, and the view then sends touches as the
        // mouse; the server draws no touch visuals of its own, since this computer already shows the touch.
        Optional("Touch input", () => connector.WithDynamicChannelRdpei(MaxTouchContacts, showTouchVisuals: false));

        // An RD Connection Broker only sends a client on to its session host if the client says it can follow, as mstsc does.
        Optional("Following an RD Connection Broker", connector.WithRedirectionSupport);
        if (options.AdminSession)
        {
            Optional("The console session", connector.WithAdminSession);
        }

        if (redirectedFrom is not null)
        {
            CarryRedirection(connector, redirectedFrom);
        }

        if (options.PlayAudio)
        {
            // Plays on the default output device and follows it when it changes, on a thread of its own.
            Optional("Remote audio", connector.WithStaticChannelRdpSndNative);
        }

        bool drivesShared = options.Drives.Count > 0 && ShareDrives(connector, options.Drives);

        if (options.PlayAudio && !drivesShared)
        {
            // Windows servers only start sound once the client has the device redirection channel, so it goes
            // along with no drive in it.
            Optional("Remote audio's device channel", () => connector.WithStaticChannelRdpdrDrives(ClientName, DriveRedirection.New()));
        }

        if (clipboard is not null)
        {
            connector.AttachStaticCliprdr(clipboard.BuildCliprdr());
        }
        if (config.DvcPipeProxy is { } dvcPipeProxy)
        {
            connector.WithDynamicChannelPipeProxy(dvcPipeProxy);
        }

        var writeBuf = WriteBuf.New();
        while (!connector.ShouldPerformSecurityUpgrade())
        {
            await Connection.SingleSequenceStep(connector, writeBuf, framed, token);
        }

        var (ssl, serverPublicKey) = await UpgradeToTlsAsync(framed, target, validator, token);
        connector.MarkSecurityUpgradeAsDone();
        var framedSsl = new Framed<SslStream>(ssl);

        // Without CredSSP the credentials would go in the clear, so a server that skips it is refused.
        if (!connector.ShouldPerformCredssp())
        {
            ssl.Dispose();
            throw new NotSupportedException("The server doesn't support Network Level Authentication, which Orrery requires.");
        }

        using (var credsspTimeout = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            credsspTimeout.CancelAfter(CredsspTimeout);
            try
            {
                await PerformCredsspAsync(connector, target.Host, writeBuf, framedSsl, serverPublicKey, credsspTimeout.Token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                ssl.Dispose();
                throw new TimeoutException("The server took too long to sign in.");
            }
            catch (Exception e) when (e is IronRdpException or IronRdpLibException { ErrorType: IronRdpLibExceptionType.EndOfFile })
            {
                // CredSSP is the login. A server that turns it down reports an error or just hangs up, and either way
                // trying the same login again by itself could lock the account, so it counts as a refused login.
                ssl.Dispose();
                throw new SessionFailureException(FailureKind.LoginFailed, "The server didn't accept the login.", e);
            }
            catch
            {
                ssl.Dispose();
                throw;
            }
        }

        while (!connector.DynState.IsTerminal())
        {
            await Connection.SingleSequenceStep(connector, writeBuf, framedSsl, token);
        }

        var state = connector.ConsumeAndCastToClientConnectorState();
        if (state.EnumType == ClientConnectorStateType.Connected)
        {
            return (state.GetConnectedResult(), framedSsl, drivesShared);
        }

        ssl.Dispose();
        throw state.EnumType == ClientConnectorStateType.Redirected
            ? new ServerRedirectedException(RdpRedirection.From(state.GetRedirectedResult()))
            : new InvalidOperationException("The RDP connection sequence failed.");
    }

    /// <summary>
    /// Asks for something the connection can do without, such as sound. If IronRDP can't, it's logged and the
    /// connection goes on: on Linux and macOS until IronRDP's native library there is built with it, say.
    /// </summary>
    static bool Optional(string what, System.Action call)
    {
        try
        {
            call();
            return true;
        }
        catch (Exception e) when (e is IronRdpException or EntryPointNotFoundException)
        {
            Logger.LogWarning("{What} is off for this connection: {Message}", what, e.Message);
            return false;
        }
    }

    /// <summary>
    /// Offers the graphics pipeline (RDPGFX), as Windows' own client does. A server that takes it resizes the desktop
    /// without restarting the session's activation, which on Windows 11 can stop its sound; one that doesn't keeps
    /// sending bitmaps. H.264 isn't offered: Windows 11 sent it as AVC420, which smeared text and its colors, while the
    /// pipeline's other codecs drew it as sharp as mstsc. For testing, <c>ORRERY_RDP_GRAPHICS_PIPELINE=0</c> leaves the
    /// pipeline off, and <c>=h264</c> offers H.264 too, where IronRDP has a decoder for it (Windows).
    /// </summary>
    static void OfferGraphicsPipeline(ClientConnector connector)
    {
        var setting = Environment.GetEnvironmentVariable("ORRERY_RDP_GRAPHICS_PIPELINE");
        if (setting == "0")
        {
            Logger.LogInformation("The graphics pipeline is off for this connection: ORRERY_RDP_GRAPHICS_PIPELINE is 0");
            return;
        }

        Optional("The graphics pipeline", () => connector.WithDynamicChannelGraphicsPipeline(h264: setting == "h264"));
    }

    /// <summary>
    /// Sends back what a redirection carried: the broker's routing token, the session to rejoin and its password
    /// cookie. Without them the broker would only redirect again, so a failure here fails the connection.
    /// </summary>
    static void CarryRedirection(ClientConnector connector, RdpRedirection from)
    {
        try
        {
            if (from.RoutingToken.Length > 0)
            {
                connector.WithLoadBalanceInfo(from.RoutingToken);
            }

            if (from.SessionId != 0)
            {
                connector.WithRedirectedSessionId(from.SessionId);
            }

            if (from.Cookie.Length > 0)
            {
                connector.WithRedirectionPasswordCookie(from.Cookie);
            }
        }
        catch (Exception e) when (e is IronRdpException or EntryPointNotFoundException)
        {
            throw new NotSupportedException(
                $"Orrery couldn't pass on what the RD Connection Broker sent ({e.Message}). Connect to the session host directly instead.", e);
        }
    }

    /// <summary>Shares the folders as drives. One that can't be opened is left out; the rest are still shared.</summary>
    static bool ShareDrives(ClientConnector connector, IReadOnlyList<SharedDrive> drives)
    {
        DriveRedirection redirection;
        try
        {
            redirection = DriveRedirection.New();
        }
        catch (Exception e) when (e is IronRdpException or EntryPointNotFoundException)
        {
            Logger.LogWarning("Shared folders are off for this connection: {Message}", e.Message);
            return false;
        }

        foreach (var drive in drives)
        {
            try
            {
                redirection.AddDrive(drive.Name, drive.Path);
            }
            catch (IronRdpException e)
            {
                Logger.LogWarning("Not sharing {Path} as {Name}: {Message}", drive.Path, drive.Name, e.Message);
            }
        }

        return redirection.Count > 0 && Optional("Shared folders", () => connector.WithStaticChannelRdpdrDrives(ClientName, redirection));
    }

    static async Task<(SslStream Stream, byte[] PublicKey)> UpgradeToTlsAsync(
        Framed<NetworkStream> framed, ConnectionTarget target, ServerCertificateValidator validator,
        CancellationToken token)
    {
        X509Certificate2? certificate = null;
        var errors = SslPolicyErrors.None;

        // Accept here and decide below: the callback is synchronous, the user prompt isn't.
        // Nothing sensitive has been sent at this point.
        var (stream, _) = framed.GetInner();
        var ssl = new SslStream(stream, leaveInnerStreamOpen: false, (_, cert, _, policyErrors) =>
        {
            certificate = cert is null ? null : new X509Certificate2(cert);
            errors = policyErrors;
            return true;
        });

        try
        {
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = target.Host,
                AllowTlsResume = false,
            }, token);

            if (certificate is null)
            {
                throw new CertificateRejectedException(target.Host);
            }

            bool trusted = await validator.ValidateAsync(target.Host, target.Port, certificate, errors);
            Orrery.Core.Diagnostics.Log.For("Rdp").Log(trusted ? LogLevel.Information : LogLevel.Warning,
                "{Host}:{Port} certificate {Subject} ({Thumbprint}), problems: {Errors}, {Decision}", target.Host, target.Port, certificate.Subject,
                certificate.GetCertHashString(), errors.ToString(), trusted ? "trusted" : "not trusted");
            if (!trusted)
            {
                throw new CertificateRejectedException(target.Host);
            }

            return (ssl, certificate.GetPublicKey());
        }
        catch
        {
            ssl.Dispose();
            throw;
        }
    }

    static async Task PerformCredsspAsync(ClientConnector connector, string host, WriteBuf writeBuf,
        Framed<SslStream> framed, byte[] serverPublicKey, CancellationToken token)
    {
        var init = CredsspSequence.Init(connector, host, serverPublicKey, null);
        var sequence = init.GetCredsspSequence();
        var tsRequest = init.GetTsRequest();
        using var kdcClient = new TcpClient();

        while (true)
        {
            ClientState clientState;
            using (var generator = sequence.ProcessTsRequest(tsRequest))
            {
                clientState = await ResolveGeneratorAsync(generator, kdcClient, token);
            }

            writeBuf.Clear();
            var written = sequence.HandleProcessResult(clientState, writeBuf);
            if (written.Size.IsSome())
            {
                var response = new byte[(int)written.Size.Get()];
                writeBuf.ReadIntoBuf(response);
                await framed.Write(response);
            }

            var hint = sequence.NextPduHint();
            if (hint is null)
            {
                return;
            }

            var pdu = await framed.ReadByHint(new CappedHint(hint), token);
            var decoded = sequence.DecodeServerMessage(pdu);
            if (decoded is null)
            {
                return;
            }

            tsRequest = decoded;
        }
    }

    /// <summary>
    /// IronRDP's hint for the next CredSSP reply, with a size cap. The hint knows each reply's format (a TsRequest, or
    /// the 4-byte Early User Authorization Result under HYBRID_EX) but would keep buffering whatever length a server
    /// claims.
    /// </summary>
    sealed class CappedHint(PduHint inner) : IPduHint
    {
        public (bool, int)? FindSize(byte[] bytes) =>
            CappedReply.FindSize(bytes, b => inner.FindSize(b) is { } found && found.IsSome() ? (long)found.Get() : null,
                MaxCredsspReply) is { } size ? (true, size) : null;
    }

    /// <summary>Runs CredSSP's network requests, which go to a Kerberos KDC when one is used.</summary>
    static async Task<ClientState> ResolveGeneratorAsync(CredsspProcessGenerator generator, TcpClient kdcClient,
        CancellationToken token)
    {
        var state = generator.Start();
        NetworkStream? kdcStream = null;

        while (true)
        {
            if (state.IsCompleted())
            {
                return state.ClientStateIfCompleted;
            }

            if (!state.IsSuspended())
            {
                throw new InvalidOperationException("CredSSP generator is neither suspended nor completed.");
            }

            var request = state.NetworkRequestIfSuspended!;
            if (request.Protocol != NetworkRequestProtocol.Tcp)
            {
                throw new NotSupportedException($"CredSSP network protocol {request.Protocol} isn't supported yet.");
            }

            if (kdcStream is null)
            {
                var url = new Uri(request.Url);
                await kdcClient.ConnectAsync(url.Host, url.Port, token);
                kdcStream = kdcClient.GetStream();
            }

            var data = request.Data;
            var payload = new byte[data.Size];
            data.Fill(payload);
            await kdcStream.WriteAsync(payload, token);

            var buffer = new byte[8192];
            int read = await kdcStream.ReadAsync(buffer, token);
            state = generator.Resume(buffer[..read]);
        }
    }

    static async Task<TcpClient> OpenTcpAsync(string host, int port, CancellationToken token)
    {
        if (!IPAddress.TryParse(host, out var address))
        {
            var entry = await Dns.GetHostEntryAsync(host, token).WaitAsync(TimeSpan.FromSeconds(10), token);
            address = entry.AddressList[0];
        }

        var client = new TcpClient(address.AddressFamily);
        try
        {
            await client.ConnectAsync(new IPEndPoint(address, port), token);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}
