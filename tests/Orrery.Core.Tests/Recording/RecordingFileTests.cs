using System.Buffers.Binary;
using System.Text;
using Orrery.Core.Credentials;
using Orrery.Core.Recording;
using Orrery.Core.Tests.Credentials;
using Xunit;

namespace Orrery.Core.Tests.Recording;

public sealed class RecordingFileTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));
    readonly byte[] _privateKey;
    readonly byte[] _publicKey;

    public RecordingFileTests()
    {
        Directory.CreateDirectory(_dir);
        (_privateKey, _publicKey) = RecordingTestKeys.New();
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    string File(string name = "a.orrec") => Path.Combine(_dir, name);

    string Write(Action<RecordingWriter> fill, bool complete = true, string name = "a.orrec")
    {
        var path = File(name);
        using var writer = RecordingWriter.Create(path, RecordingKind.Terminal, _publicKey);
        fill(writer);
        if (complete)
        {
            writer.Complete(TimeSpan.FromSeconds(9));
        }

        return path;
    }

    List<RecordedEvent> ReadAll(string path, out RecordingEnding ending)
    {
        using var reader = RecordingReader.Open(path, _privateKey);
        var events = reader.Events().ToList();
        ending = reader.Ending;
        return events;
    }

    [Fact]
    public void Events_come_back_as_written_and_a_closed_file_says_so()
    {
        var info = new RecordingInfo("web-01", "SSH", DateTimeOffset.Parse("2026-11-01T14:02:33Z"));
        var path = Write(w =>
        {
            w.Append(RecordingEventType.Info, TimeSpan.Zero, RecordingFile.InfoPayload(info));
            w.Append(RecordingEventType.Output, TimeSpan.FromMilliseconds(250), "hello\xff"u8);
            w.Append(RecordingEventType.Resize, TimeSpan.FromSeconds(1), RecordingFile.Pair(120, 40));
        });

        var events = ReadAll(path, out var ending);
        Assert.Equal(RecordingEnding.Closed, ending);
        Assert.Equal([RecordingEventType.Info, RecordingEventType.Output, RecordingEventType.Resize, RecordingEventType.End], events.Select(e => e.Type));
        Assert.Equal(info, RecordingFile.ReadInfo(events[0].Payload));
        Assert.Equal("hello\xff"u8.ToArray(), events[1].Payload);
        Assert.Equal(TimeSpan.FromMilliseconds(250), events[1].Time);
        Assert.Equal((120, 40), RecordingFile.ReadPair(events[2].Payload));
    }

    [Fact]
    public void How_a_recording_is_colored_is_kept_and_older_ones_read_without_it()
    {
        var info = new RecordingInfo("router", "HTTPS", DateTimeOffset.Parse("2026-11-03T09:00:00Z"), ColorCorrection: true,
            CorrectedFor: Orrery.Core.Settings.ColorVision.Deuteranopia, CorrectedStrength: 70);
        Assert.Equal(info, RecordingFile.ReadInfo(RecordingFile.InfoPayload(info)));

        var older = RecordingFile.ReadInfo("""{"name":"web-01","protocol":"SSH","started":"2026-11-01T14:02:33+00:00"}"""u8.ToArray());
        Assert.Equal(new RecordingInfo("web-01", "SSH", DateTimeOffset.Parse("2026-11-01T14:02:33Z")), older);
    }

    [Fact]
    public void Events_larger_than_a_chunk_cross_chunks_and_can_be_resumed_from()
    {
        var big = Enumerable.Range(0, RecordingFile.ChunkSize * 3 + 17).Select(i => (byte)i).ToArray();
        var path = Write(w =>
        {
            w.Append(RecordingEventType.Output, TimeSpan.Zero, "first"u8);
            w.Append(RecordingEventType.Output, TimeSpan.FromSeconds(1), big);
            w.Append(RecordingEventType.Output, TimeSpan.FromSeconds(2), "last"u8);
        });

        using var reader = RecordingReader.Open(path, _privateKey);
        var events = reader.Events().ToList();
        Assert.Equal(big, events[1].Payload);

        var resumed = reader.Events(events[2].Position).ToList();
        Assert.Equal("last"u8.ToArray(), resumed[0].Payload);
        Assert.Equal(RecordingEnding.Closed, reader.Ending);
    }

    [Fact]
    public void A_file_never_closed_plays_up_to_its_last_flush()
    {
        var path = File();
        var writer = RecordingWriter.Create(path, RecordingKind.Terminal, _publicKey);
        writer.Append(RecordingEventType.Output, TimeSpan.Zero, "kept"u8);
        writer.Flush();
        writer.Dispose();

        var events = ReadAll(path, out var ending);
        Assert.Equal(RecordingEnding.Unclosed, ending);
        Assert.Equal("kept"u8.ToArray(), Assert.Single(events).Payload);
    }

    [Fact]
    public void A_file_cut_off_mid_chunk_reads_as_unclosed()
    {
        var path = Write(w =>
        {
            w.Append(RecordingEventType.Output, TimeSpan.Zero, "one"u8);
            w.Flush();
            w.Append(RecordingEventType.Output, TimeSpan.Zero, "two"u8);
        });
        var bytes = System.IO.File.ReadAllBytes(path);
        System.IO.File.WriteAllBytes(path, bytes[..^10]);

        var events = ReadAll(path, out var ending);
        Assert.Equal(RecordingEnding.Unclosed, ending);
        Assert.Equal("one"u8.ToArray(), Assert.Single(events).Payload);
    }

    [Fact]
    public void A_changed_byte_stops_reading_at_that_chunk()
    {
        var path = Write(w =>
        {
            w.Append(RecordingEventType.Output, TimeSpan.Zero, "one"u8);
            w.Flush();
            w.Append(RecordingEventType.Output, TimeSpan.Zero, "two"u8);
        });
        var bytes = System.IO.File.ReadAllBytes(path);
        bytes[^3] ^= 1;
        System.IO.File.WriteAllBytes(path, bytes);

        var events = ReadAll(path, out var ending);
        Assert.Equal(RecordingEnding.Damaged, ending);
        Assert.Single(events);
    }

    [Fact]
    public void Chunks_swapped_or_dropped_are_caught()
    {
        var path = Write(w =>
        {
            foreach (var word in new[] { "one", "two", "three" })
            {
                w.Append(RecordingEventType.Output, TimeSpan.Zero, Encoding.ASCII.GetBytes(word));
                w.Flush();
            }
        });
        var (preamble, chunks) = Split(System.IO.File.ReadAllBytes(path));

        System.IO.File.WriteAllBytes(path, [.. preamble, .. chunks[1], .. chunks[0], .. chunks[2], .. chunks[3]]);
        Assert.Empty(ReadAll(path, out var swapped));
        Assert.Equal(RecordingEnding.Damaged, swapped);

        System.IO.File.WriteAllBytes(path, [.. preamble, .. chunks[0], .. chunks[2], .. chunks[3]]);
        Assert.Single(ReadAll(path, out var dropped));
        Assert.Equal(RecordingEnding.Damaged, dropped);
    }

    [Fact]
    public void Marking_an_early_chunk_as_the_last_is_caught()
    {
        var path = Write(w =>
        {
            w.Append(RecordingEventType.Output, TimeSpan.Zero, "one"u8);
            w.Flush();
            w.Append(RecordingEventType.Output, TimeSpan.Zero, "two"u8);
        });
        var (preamble, chunks) = Split(System.IO.File.ReadAllBytes(path));
        chunks[0][0] = RecordingFile.LastFlag;

        System.IO.File.WriteAllBytes(path, [.. preamble, .. chunks[0]]);
        Assert.Empty(ReadAll(path, out var ending));
        Assert.Equal(RecordingEnding.Damaged, ending);
    }

    [Fact]
    public void A_chunk_moved_from_another_recording_is_caught()
    {
        var first = Write(w => w.Append(RecordingEventType.Output, TimeSpan.Zero, "mine"u8), name: "1.orrec");
        var second = Write(w => w.Append(RecordingEventType.Output, TimeSpan.Zero, "theirs"u8), name: "2.orrec");
        var (preamble, _) = Split(System.IO.File.ReadAllBytes(first));
        var (_, theirs) = Split(System.IO.File.ReadAllBytes(second));

        System.IO.File.WriteAllBytes(first, [.. preamble, .. theirs[0]]);
        Assert.Empty(ReadAll(first, out var ending));
        Assert.Equal(RecordingEnding.Damaged, ending);
    }

    [Fact]
    public void Another_key_is_refused_before_anything_is_read()
    {
        var path = Write(w => w.Append(RecordingEventType.Output, TimeSpan.Zero, "x"u8));
        var (otherPrivate, _) = RecordingTestKeys.New();

        Assert.Throws<RecordingKeyException>(() => RecordingReader.Open(path, otherPrivate));
    }

    [Fact]
    public void The_header_says_nothing_about_the_host_and_editing_it_is_caught()
    {
        var path = Write(w => w.Append(RecordingEventType.Info, TimeSpan.Zero,
            RecordingFile.InfoPayload(new RecordingInfo("secret-host-name", "SSH", DateTimeOffset.UnixEpoch))));
        var text = Encoding.UTF8.GetString(System.IO.File.ReadAllBytes(path));
        Assert.DoesNotContain("secret-host-name", text);
        Assert.Equal(RecordingKind.Terminal, RecordingReader.ReadHeader(path).Kind);

        // "terminal" → "screen", padded to the same length: the header still parses, but the chunks no longer open.
        var bytes = System.IO.File.ReadAllBytes(path);
        var at = Encoding.UTF8.GetString(bytes).IndexOf("\"terminal\"", StringComparison.Ordinal);
        Encoding.ASCII.GetBytes("\"screen\"  ").CopyTo(bytes, at);
        System.IO.File.WriteAllBytes(path, bytes);
        Assert.Equal(RecordingKind.Screen, RecordingReader.ReadHeader(path).Kind);
        Assert.Empty(ReadAll(path, out var ending));
        Assert.Equal(RecordingEnding.Damaged, ending);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(RecordingFile.MaxHeaderSize + 1)]
    [InlineData(-5)]
    public void Header_lengths_outside_the_limits_are_refused(int length)
    {
        var path = File();
        var bytes = new byte[10];
        "ORREC\u0001"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(6), length);
        System.IO.File.WriteAllBytes(path, bytes);

        Assert.Throws<InvalidDataException>(() => RecordingReader.ReadHeader(path));
    }

    [Fact]
    public void Something_else_is_not_a_recording()
    {
        var path = File();
        System.IO.File.WriteAllText(path, "{\"format\":\"orrery-vault\"}");
        Assert.Throws<InvalidDataException>(() => RecordingReader.ReadHeader(path));
    }

    [Fact]
    public void A_recording_being_written_can_be_read_meanwhile()
    {
        var path = File();
        using var writer = RecordingWriter.Create(path, RecordingKind.Terminal, _publicKey);
        writer.Append(RecordingEventType.Output, TimeSpan.Zero, "so far"u8);
        writer.Flush();

        var events = ReadAll(path, out var ending);
        Assert.Equal(RecordingEnding.Unclosed, ending);
        Assert.Single(events);
    }

    // Splits a file into what comes before the chunks and the chunks, each with its flags and length.
    static (byte[] Preamble, List<byte[]> Chunks) Split(byte[] file)
    {
        int headerLength = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(6));
        int at = 10 + headerLength;
        var chunks = new List<byte[]>();
        while (at < file.Length)
        {
            int length = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(at + 1));
            chunks.Add(file[at..(at + 5 + length)]);
            at += 5 + length;
        }

        return (file[..(10 + headerLength)], chunks);
    }
}

static class RecordingTestKeys
{
    public static (byte[] Private, byte[] Public) New()
    {
        var index = Path.Combine(Path.GetTempPath(), "orrery-keys-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new KeychainCredentialStore(index, new MemorySecrets());
            var publicKey = RecordingKeys.EnsureAsync(store).GetAwaiter().GetResult();
            var privateKey = RecordingKeys.PrivateKeyAsync(store).GetAwaiter().GetResult()!;
            return (privateKey, publicKey);
        }
        finally
        {
            File.Delete(index);
        }
    }
}
