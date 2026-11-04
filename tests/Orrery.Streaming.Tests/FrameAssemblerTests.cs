using System.Net;
using Xunit;

namespace Orrery.Streaming.Tests;

public class FrameAssemblerTests
{
    static readonly MediaPacketHeader Template = new(MediaKind.Video, MediaPacketFlags.Recovery, 0, 1, 0, 0, 123_456);

    static byte[] Frame(int length, int seed = 1) => Enumerable.Range(0, length).Select(i => (byte)(i * 31 + seed)).ToArray();

    static List<byte[]> Split(byte[] frame, MediaPacketHeader header, int maxDatagram = OspProtocol.MaxDatagramPayload)
    {
        var datagrams = new List<byte[]>();
        FramePacketizer.Split(frame, header, new byte[maxDatagram], d => datagrams.Add(d.ToArray()));
        return datagrams;
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1180, 1)]
    [InlineData(1181, 2)]
    [InlineData(100_000, 85)]
    public void A_frame_needs_one_packet_per_1180_bytes(int length, int expected)
    {
        Assert.Equal(expected, FramePacketizer.PacketCount(length));
        Assert.Equal(expected, Split(Frame(length), Template).Count);
    }

    [Fact]
    public void Datagrams_fit_the_limit_and_number_themselves()
    {
        var datagrams = Split(Frame(5000), Template);
        Assert.All(datagrams, d => Assert.InRange(d.Length, MediaPacketHeader.Size + 1, OspProtocol.MaxDatagramPayload));
        for (int i = 0; i < datagrams.Count; i++)
        {
            Assert.True(MediaPacketHeader.TryRead(datagrams[i], out var header));
            Assert.Equal(Template with { PacketIndex = (ushort)i, PacketCount = (ushort)datagrams.Count }, header);
        }
    }

    [Fact]
    public void A_frame_comes_back_whole_in_any_order()
    {
        byte[] frame = Frame(10_000);
        var datagrams = Split(frame, Template);
        datagrams.Reverse();
        var assembler = new FrameAssembler();

        AssembledFrame? result = null;
        foreach (var d in datagrams)
        {
            Assert.Null(result);
            result = assembler.Add(d);
        }

        Assert.NotNull(result);
        Assert.Equal(frame, result.Data);
        Assert.Equal((MediaKind.Video, MediaPacketFlags.Recovery, (byte)0, 1u, 123_456ul), (result.Kind, result.Flags, result.Stream, result.FrameNumber, result.CaptureMicroseconds));
        Assert.Equal(1, assembler.FramesCompleted);
    }

    [Fact]
    public void Duplicates_are_counted_and_ignored()
    {
        var datagrams = Split(Frame(3000), Template);
        var assembler = new FrameAssembler();
        assembler.Add(datagrams[0]);
        assembler.Add(datagrams[0]);
        assembler.Add(datagrams[1]);

        Assert.NotNull(assembler.Add(datagrams[2]));
        Assert.Equal(1, assembler.PacketsDuplicated);
        // Once complete, a late copy is for a finished frame.
        Assert.Null(assembler.Add(datagrams[1]));
        Assert.Equal(1, assembler.PacketsRejected);
    }

    [Fact]
    public void A_newer_frame_completing_drops_an_older_one_missing_packets()
    {
        var older = Split(Frame(3000), Template with { FrameNumber = 5 });
        var newer = Split(Frame(100, 2), Template with { FrameNumber = 6 });
        var assembler = new FrameAssembler();
        assembler.Add(older[0]);
        assembler.Add(older[1]);

        Assert.NotNull(assembler.Add(newer[0]));
        Assert.Equal(1, assembler.FramesDropped);
        Assert.Null(assembler.Add(older[2]));
        Assert.Equal(1, assembler.PacketsRejected);
    }

    [Fact]
    public void Streams_are_assembled_separately()
    {
        var video = Split(Frame(2000), Template with { Stream = 0, FrameNumber = 9 });
        var other = Split(Frame(100), Template with { Stream = 1, FrameNumber = 10 });
        var assembler = new FrameAssembler();
        assembler.Add(video[0]);

        Assert.NotNull(assembler.Add(other[0]));
        Assert.NotNull(assembler.Add(video[1]));
        Assert.Equal(0, assembler.FramesDropped);
    }

    [Fact]
    public void Frame_numbers_may_wrap()
    {
        var assembler = new FrameAssembler();
        Assert.NotNull(assembler.Add(Split(Frame(10), Template with { FrameNumber = uint.MaxValue })[0]));
        Assert.NotNull(assembler.Add(Split(Frame(10), Template with { FrameNumber = 0 })[0]));
    }

    [Fact]
    public void A_packet_disagreeing_with_its_frame_is_rejected()
    {
        var datagrams = Split(Frame(3000), Template);
        var assembler = new FrameAssembler();
        assembler.Add(datagrams[0]);
        var liar = Split(Frame(5000), Template)[1]; // same frame number, but says 5 packets, not 3

        Assert.Null(assembler.Add(liar));
        Assert.Equal(1, assembler.PacketsRejected);
        Assert.Null(assembler.Add(datagrams[1]));
        Assert.NotNull(assembler.Add(datagrams[2]));
    }

    [Fact]
    public void Malformed_and_parity_datagrams_are_rejected()
    {
        var assembler = new FrameAssembler();
        var parity = Split(Frame(10), Template with { Flags = MediaPacketFlags.Parity })[0];

        Assert.Null(assembler.Add(new byte[5]));
        Assert.Null(assembler.Add(parity));
        Assert.Null(assembler.Add(parity.AsSpan(0, MediaPacketHeader.Size)));
        Assert.Equal(3, assembler.PacketsRejected);
    }

    [Fact]
    public void A_frame_over_the_size_limit_is_given_up()
    {
        var assembler = new FrameAssembler(maxFrameBytes: 2000);
        var datagrams = Split(Frame(3000), Template);
        assembler.Add(datagrams[0]);

        Assert.Null(assembler.Add(datagrams[1]));
        Assert.Equal(1, assembler.FramesDropped);
        Assert.Equal(1, assembler.PacketsRejected);
    }

    [Fact]
    public void Too_many_waiting_frames_drop_the_oldest()
    {
        var assembler = new FrameAssembler();
        for (uint n = 1; n <= FrameAssembler.MaxFramesInProgress + 1; n++)
        {
            assembler.Add(Split(Frame(2000), Template with { FrameNumber = n })[0]);
        }

        Assert.Equal(1, assembler.FramesDropped);
    }

    [Fact]
    public void Frames_cross_real_UDP_on_loopback()
    {
        using var receiver = new UdpMediaReceiver(new IPEndPoint(IPAddress.Loopback, 0));
        var received = new List<AssembledFrame>();
        var done = new ManualResetEventSlim();
        var thread = new Thread(() => receiver.Run(f =>
        {
            received.Add(f);
            if (received.Count == 3)
            {
                done.Set();
            }
        }));
        thread.Start();

        var frames = new[] { Frame(50_000, 1), Frame(10, 2), Frame(3000, 3) };
        using (var sender = new UdpMediaSender(receiver.LocalEndPoint))
        {
            for (int i = 0; i < frames.Length; i++)
            {
                sender.SendFrame(frames[i], Template with { FrameNumber = (uint)i + 1 });
            }
        }

        Assert.True(done.Wait(TimeSpan.FromSeconds(5)));
        receiver.Dispose();
        thread.Join();
        Assert.Equal(frames, received.Select(f => f.Data));
    }
}
