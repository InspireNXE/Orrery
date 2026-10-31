using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Devolutions.IronRdp;
using Orrery.Core.Security;
using Orrery.Core.Sessions;

namespace Orrery.Protocols.Rdp;

public sealed class CertificateRejectedException(string host)
    : Exception($"The certificate presented by {host} was not trusted.");

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

    /// <param name="dial">Where to open the connection: the target itself, or a tunnel's local end. TLS, the
    /// certificate check and CredSSP still use <paramref name="target"/>'s name.</param>
    public static async Task<(ConnectionResult Result, Framed<SslStream> Framed)> ConnectAsync(
        Config config, ConnectionTarget target, (string Host, int Port) dial, ServerCertificateValidator validator, CliprdrBackendFactory? clipboard,
        CancellationToken token)
    {
        var tcp = await OpenTcpAsync(dial.Host, dial.Port, token);
        var framed = new Framed<NetworkStream>(tcp.GetStream());

        var connector = ClientConnector.New(config, tcp.Client.LocalEndPoint!.ToString()!);
        connector.WithDynamicChannelDisplayControl();
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
            return (state.GetConnectedResult(), framedSsl);
        }

        ssl.Dispose();
        throw state.EnumType == ClientConnectorStateType.Redirected
            ? new NotSupportedException("The server redirected the connection, which Orrery doesn't support yet.")
            : new InvalidOperationException("The RDP connection sequence failed.");
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

            if (!await validator.ValidateAsync(target.Host, target.Port, certificate, errors))
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
