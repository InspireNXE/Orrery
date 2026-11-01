using System.Collections.Concurrent;
using System.Text;
using Orrery.Core.Security;
using Orrery.Core.Sessions;
using Xunit;
using static Orrery.Protocols.Rdp.RdpClipboardBridge;

namespace Orrery.Protocols.Rdp.Tests;

public class RdpClipboardBridgeTests
{
    /// <summary>Plays the server's side of IronRDP's callback clipboard and records what the bridge sends.</summary>
    sealed class FakeChannel : IRdpClipboardChannel
    {
        public ConcurrentQueue<RdpClipboardEvent> Events { get; } = new();
        public ConcurrentQueue<uint[]> Announced { get; } = new();
        public ConcurrentQueue<uint> Requested { get; } = new();
        public ConcurrentQueue<byte[]?> Sent { get; } = new();

        public RdpClipboardEvent? NextEvent(TimeSpan timeout)
        {
            if (Events.TryDequeue(out var next))
            {
                return next;
            }

            Thread.Sleep(10);
            return null;
        }

        public Task AnnounceFormatsAsync(IReadOnlyList<uint> formats, CancellationToken token)
        {
            Announced.Enqueue([.. formats]);
            return Task.CompletedTask;
        }

        public Task RequestDataAsync(uint format, CancellationToken token)
        {
            Requested.Enqueue(format);
            return Task.CompletedTask;
        }

        public Task SendDataAsync(byte[]? data, CancellationToken token)
        {
            Sent.Enqueue(data);
            return Task.CompletedTask;
        }
    }

    readonly FakeChannel _channel = new();
    readonly RdpClipboardBridge _bridge;
    readonly List<string> _remoteText = [];

    public RdpClipboardBridgeTests()
    {
        _bridge = new RdpClipboardBridge(_channel);
        _bridge.RemoteTextChanged += (_, text) => _remoteText.Add(text);
    }

    Task Handle(RdpClipboardEventKind kind, IReadOnlyList<uint>? formats = null, uint format = 0, byte[]? data = null) =>
        _bridge.HandleAsync(new RdpClipboardEvent(kind, formats, format, data), CancellationToken.None);

    static byte[] Utf16(string text) => Encoding.Unicode.GetBytes(text + '\0');

    async Task ReceiveAsync(uint format, byte[]? data)
    {
        await Handle(RdpClipboardEventKind.RemoteFormatList, [format]);
        await Handle(RdpClipboardEventKind.FormatDataResponse, data: data);
    }

    [Fact]
    public async Task Announces_no_formats_while_the_local_clipboard_is_empty()
    {
        await Handle(RdpClipboardEventKind.FormatListRequested);

        Assert.Empty(Assert.Single(_channel.Announced));
    }

    [Fact]
    public async Task Announces_local_text_and_answers_the_server_in_both_text_formats()
    {
        _bridge.SetLocalText("café ☕");
        await Handle(RdpClipboardEventKind.FormatListRequested);
        await Handle(RdpClipboardEventKind.FormatDataRequested, format: CfUnicodeText);
        await Handle(RdpClipboardEventKind.FormatDataRequested, format: CfText);
        await Handle(RdpClipboardEventKind.FormatDataRequested, format: 8); // CF_DIB: no image here.

        Assert.Equal([CfUnicodeText, CfText], Assert.Single(_channel.Announced));
        var sent = _channel.Sent.ToArray();
        Assert.Equal(Utf16("café ☕"), sent[0]);
        Assert.Equal(Encoding.Latin1.GetBytes("café ?\0"), sent[1]); // ☕ isn't Latin-1.
        Assert.Null(sent[2]);
    }

    [Fact]
    public async Task Answers_a_request_with_no_data_when_there_is_no_local_text()
    {
        await Handle(RdpClipboardEventKind.FormatDataRequested, format: CfUnicodeText);

        Assert.Null(Assert.Single(_channel.Sent));
    }

    [Fact]
    public async Task Announces_text_copied_after_the_channel_is_ready()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        _channel.Events.Enqueue(new(RdpClipboardEventKind.FormatListRequested));
        _channel.Events.Enqueue(new(RdpClipboardEventKind.Ready));
        var run = Task.Run(() => _bridge.RunAsync(cts.Token));

        await WaitUntilAsync(() => _channel.Announced.Count == 1, cts.Token);
        _bridge.SetLocalText("copied later");
        await WaitUntilAsync(() => _channel.Announced.Count == 2, cts.Token);
        _bridge.SetLocalText("copied later"); // Unchanged: not announced again.
        await Task.Delay(100);
        cts.Cancel();
        await run;

        Assert.Equal(2, _channel.Announced.Count);
        Assert.Equal([CfUnicodeText, CfText], _channel.Announced.Last());
    }

    [Fact]
    public async Task Stops_when_the_session_does()
    {
        bool connected = true;
        var bridge = new RdpClipboardBridge(_channel, () => connected);
        var run = Task.Run(() => bridge.RunAsync(CancellationToken.None));

        connected = false;

        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Asks_for_unicode_text_first_then_ansi_text_and_nothing_else()
    {
        await Handle(RdpClipboardEventKind.RemoteFormatList, [CfText, CfUnicodeText, 0xC004]);
        await Handle(RdpClipboardEventKind.RemoteFormatList, [CfText]);
        await Handle(RdpClipboardEventKind.RemoteFormatList, [8, 0xC004]);
        await Handle(RdpClipboardEventKind.RemoteFormatList, []);

        Assert.Equal([CfUnicodeText, CfText], _channel.Requested.ToArray());
    }

    [Fact]
    public async Task Raises_the_servers_text_up_to_its_null_and_does_not_offer_it_back()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        _channel.Events.Enqueue(new(RdpClipboardEventKind.Ready));
        _channel.Events.Enqueue(new(RdpClipboardEventKind.RemoteFormatList, [CfUnicodeText]));
        _channel.Events.Enqueue(new(RdpClipboardEventKind.FormatDataResponse, Data: [.. Utf16("from the server"), .. Utf16("junk after the null")]));
        var run = Task.Run(() => _bridge.RunAsync(cts.Token));

        await WaitUntilAsync(() => _remoteText.Count == 1, cts.Token);
        await Task.Delay(100);
        cts.Cancel();
        await run;

        Assert.Equal("from the server", Assert.Single(_remoteText));
        Assert.Empty(_channel.Announced);
        await Handle(RdpClipboardEventKind.FormatDataRequested, format: CfUnicodeText);
        Assert.Equal(Utf16("from the server"), Assert.Single(_channel.Sent));
    }

    [Fact]
    public async Task Pairs_each_response_with_the_format_asked_for()
    {
        await Handle(RdpClipboardEventKind.RemoteFormatList, [CfUnicodeText]);
        await Handle(RdpClipboardEventKind.RemoteFormatList, [CfText]);
        await Handle(RdpClipboardEventKind.FormatDataResponse, data: Utf16("unicode"));
        await Handle(RdpClipboardEventKind.FormatDataResponse, data: "ansi\0"u8.ToArray());

        Assert.Equal(["unicode", "ansi"], _remoteText);
    }

    [Fact]
    public async Task Ignores_a_response_nobody_asked_for_and_a_failed_one()
    {
        await Handle(RdpClipboardEventKind.FormatDataResponse, data: Utf16("unasked"));
        await ReceiveAsync(CfUnicodeText, null);
        await Handle(RdpClipboardEventKind.FormatDataResponse, data: Utf16("after the failure"));

        Assert.Empty(_remoteText);
    }

    [Fact]
    public async Task Remembers_at_most_four_unanswered_requests()
    {
        for (int i = 0; i < 4; i++)
        {
            await Handle(RdpClipboardEventKind.RemoteFormatList, [CfText]);
        }

        // The fifth pushes out the oldest, so the fourth response is read as UTF-16 and the fifth is unasked.
        await Handle(RdpClipboardEventKind.RemoteFormatList, [CfUnicodeText]);
        for (int i = 0; i < 4; i++)
        {
            await Handle(RdpClipboardEventKind.FormatDataResponse, data: "ab\0"u8.ToArray());
        }

        await Handle(RdpClipboardEventKind.FormatDataResponse, data: "b\0"u8.ToArray());

        Assert.Equal(5, _channel.Requested.Count);
        Assert.Equal(["ab", "ab", "ab", "扡"], _remoteText);
    }

    [Fact]
    public void Decodes_bad_unicode_with_replacement_characters()
    {
        // A lone high surrogate, then an odd trailing byte.
        byte[] data = [0x41, 0x00, 0x00, 0xD8, 0x42, 0x00, 0x43];

        Assert.Equal("A�B", Decode(CfUnicodeText, data));
    }

    [Fact]
    public void Refuses_empty_and_oversized_server_text()
    {
        Assert.Null(Decode(CfUnicodeText, [0x00, 0x00]));
        Assert.Null(Decode(CfText, []));
        Assert.Null(Decode(CfUnicodeText, new byte[MaxDataBytes + 2]));
        Assert.Null(Decode(CfText, Enumerable.Repeat((byte)'x', MaxTextLength + 1).ToArray()));
        Assert.Equal(MaxTextLength, Decode(CfUnicodeText, Utf16(new string('x', MaxTextLength)))!.Length);
    }

    [Fact]
    public async Task Does_not_share_local_text_over_the_limit()
    {
        var refused = new List<string>();
        _bridge.TextRefused += (_, reason) => refused.Add(reason);
        _bridge.SetLocalText(new string('x', MaxTextLength + 1));
        await Handle(RdpClipboardEventKind.FormatListRequested);

        Assert.Empty(Assert.Single(_channel.Announced));
        Assert.Contains("wasn't sent to the remote desktop: 1,048,577 characters is over the 1,048,576-character limit", Assert.Single(refused));
    }

    [Fact]
    public async Task Says_when_the_servers_text_is_over_the_limit()
    {
        var refused = new List<string>();
        _bridge.TextRefused += (_, reason) => refused.Add(reason);
        await ReceiveAsync(CfUnicodeText, new byte[MaxDataBytes + 2]);

        Assert.Empty(_remoteText);
        Assert.Contains("from the remote desktop wasn't copied here", Assert.Single(refused));

        // Within the limit, nothing is said.
        await ReceiveAsync(CfUnicodeText, Utf16("fine"));
        Assert.Single(refused);
    }

    [Fact]
    public async Task A_session_with_clipboard_sharing_off_exchanges_nothing()
    {
        var validator = new ServerCertificateValidator(new NoTrustStore(), _ => Task.FromResult(default(CertificateTrustDecision)));
        await using var session = RdpSession.Start(new ConnectionTarget("127.0.0.1", 1, "user", null), "password", 1024, 768, validator,
            shareClipboard: false);

        Assert.False(((IClipboardSession)session).ExchangesClipboard);
    }

    sealed class NoTrustStore : ICertificateTrustStore
    {
        public PinnedCertificate? Get(string host, int port) => null;
        public void Pin(string host, int port, PinnedCertificate certificate) { }
    }

    static async Task WaitUntilAsync(Func<bool> condition, CancellationToken token)
    {
        while (!condition())
        {
            await Task.Delay(10, token);
        }
    }
}
