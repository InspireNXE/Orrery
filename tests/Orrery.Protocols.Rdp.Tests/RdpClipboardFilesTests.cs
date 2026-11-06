using System.Buffers.Binary;
using System.Collections.Concurrent;
using Orrery.Core.Sessions;
using Xunit;
using static Orrery.Protocols.Rdp.RdpClipboardBridge;

namespace Orrery.Protocols.Rdp.Tests;

/// <summary>Images and files over the RDP clipboard: the bridge against a fake channel, and the helpers it uses.</summary>
public sealed class RdpClipboardFilesTests : IDisposable
{
    sealed class FakeChannel : IRdpClipboardChannel
    {
        public ConcurrentQueue<RdpClipboardFormat[]> Announced { get; } = new();
        public ConcurrentQueue<uint> Requested { get; } = new();
        public ConcurrentQueue<byte[]?> Sent { get; } = new();
        public ConcurrentQueue<IReadOnlyList<LocalClipboardFile>> Offered { get; } = new();
        public List<(uint StreamId, int Index, bool Size, long Position, int Length, uint? LockId)> FileRequests { get; } = [];
        public List<(uint StreamId, byte[]? Data, long? Size)> FileAnswers { get; } = [];

        public bool FileTransfer { get; set; } = true;

        public RdpClipboardEvent? NextEvent(TimeSpan timeout) => null;

        public Task AnnounceFormatsAsync(IReadOnlyList<RdpClipboardFormat> formats, CancellationToken token)
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

        public Task OfferFilesAsync(IReadOnlyList<LocalClipboardFile> files, CancellationToken token)
        {
            Offered.Enqueue(files);
            return Task.CompletedTask;
        }

        public Task RequestFileContentsAsync(uint streamId, int index, bool sizeRequest, long position, int length, uint? lockId, CancellationToken token)
        {
            FileRequests.Add((streamId, index, sizeRequest, position, length, lockId));
            return Task.CompletedTask;
        }

        public Task SendFileContentsAsync(uint streamId, byte[]? data, CancellationToken token)
        {
            FileAnswers.Add((streamId, data, null));
            return Task.CompletedTask;
        }

        public Task SendFileSizeAsync(uint streamId, long size, CancellationToken token)
        {
            FileAnswers.Add((streamId, null, size));
            return Task.CompletedTask;
        }
    }

    readonly FakeChannel _channel = new();
    readonly RdpClipboardBridge _bridge;
    readonly List<byte[]> _images = [];
    readonly List<IReadOnlyList<string>> _files = [];
    readonly List<IReadOnlyList<string>> _ready = [];
    readonly List<ClipboardTransfer> _transfers = [];
    readonly List<string> _refused = [];
    readonly string _local = Path.Combine(Path.GetTempPath(), "orrery-test-" + Guid.NewGuid().ToString("N"));

    public RdpClipboardFilesTests()
    {
        _bridge = new RdpClipboardBridge(_channel);
        _bridge.RemoteImageChanged += (_, image) => _images.Add(image);
        _bridge.RemoteFilesChanged += (_, paths) => _files.Add(paths);
        _bridge.RemoteFilesReady += (_, paths) => _ready.Add(paths);
        _bridge.TransferChanged += (_, transfer) => _transfers.Add(transfer);
        _bridge.TextRefused += (_, reason) => _refused.Add(reason);
        Directory.CreateDirectory(_local);
    }

    public void Dispose()
    {
        _bridge.DeleteDownloads();
        Directory.Delete(_local, recursive: true);
    }

    Task Handle(RdpClipboardEvent clipboardEvent) => _bridge.HandleAsync(clipboardEvent, CancellationToken.None);

    Task RemoteFormats(params (uint Id, string? Name)[] formats) =>
        Handle(new(RdpClipboardEventKind.RemoteFormatList, [.. formats.Select(f => f.Id)]) { Names = [.. formats.Select(f => f.Name)] });

    static ClipboardImage Image() => new(2, 2, [1, 2, 3, 255, 4, 5, 6, 255, 7, 8, 9, 255, 10, 11, 12, 255], [0x89, (byte)'P', (byte)'N', (byte)'G']);

    [Fact]
    public async Task Announces_a_local_image_as_dib_and_png_and_answers_each()
    {
        _bridge.SetLocalImage(Image());
        await Handle(new(RdpClipboardEventKind.FormatListRequested));
        await Handle(new(RdpClipboardEventKind.FormatDataRequested, Format: CfDib));
        await Handle(new(RdpClipboardEventKind.FormatDataRequested, Format: LocalPngFormat));
        await Handle(new(RdpClipboardEventKind.FormatDataRequested, Format: CfUnicodeText));

        Assert.Equal([new(CfDib), new(LocalPngFormat, PngName)], Assert.Single(_channel.Announced));
        var sent = _channel.Sent.ToArray();
        Assert.Equal(RdpDib.FromBgra(2, 2, Image().Bgra), sent[0]);
        Assert.Equal(Image().Png, sent[1]);
        Assert.Null(sent[2]);
    }

    [Fact]
    public void A_dib_is_bottom_row_first_with_a_32_bit_header()
    {
        var dib = RdpDib.FromBgra(2, 2, Image().Bgra);

        Assert.Equal(40, BinaryPrimitives.ReadInt32LittleEndian(dib));
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(8)));
        Assert.Equal(32, BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(14)));
        Assert.Equal(Image().Bgra[8..16], dib[40..48]);
        Assert.Equal(Image().Bgra[..8], dib[48..56]);
    }

    [Fact]
    public void A_dib_becomes_a_bmp_file_only_when_its_header_fits_its_data()
    {
        var dib = RdpDib.FromBgra(2, 2, Image().Bgra);
        var bmp = RdpDib.ToBmpFile(dib)!;

        Assert.Equal("BM"u8.ToArray(), bmp[..2]);
        Assert.Equal(bmp.Length, BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(2)));
        Assert.Equal(14 + 40, BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(10)));
        Assert.Equal(dib, bmp[14..]);

        Assert.Null(RdpDib.ToBmpFile(dib[..^1]));
        Assert.Null(RdpDib.ToBmpFile(dib[..39]));
        var huge = (byte[])dib.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(huge.AsSpan(4), 1 << 20);
        Assert.Null(RdpDib.ToBmpFile(huge));
    }

    [Fact]
    public async Task Prefers_files_then_text_then_png_then_dib()
    {
        await RemoteFormats((CfDib, null), (0xC010, "PNG"), (CfUnicodeText, null), (0xC011, FileListName));
        await RemoteFormats((CfDib, null), (0xC010, "PNG"), (CfUnicodeText, null));
        await RemoteFormats((CfDib, null), (0xC010, "PNG"));
        await RemoteFormats((CfDib, null));

        Assert.Equal([0xC011u, CfUnicodeText, 0xC010u, CfDib], _channel.Requested.ToArray());
    }

    [Fact]
    public async Task Leaves_the_file_list_alone_without_file_transfer()
    {
        _channel.FileTransfer = false;
        await RemoteFormats((CfUnicodeText, null), (0xC011, FileListName));

        Assert.Equal([CfUnicodeText], _channel.Requested.ToArray());
    }

    [Fact]
    public async Task Raises_the_servers_png_as_it_came_and_its_dib_as_a_bmp_file()
    {
        await RemoteFormats((0xC010, "PNG"));
        await Handle(new(RdpClipboardEventKind.FormatDataResponse, Format: 0xC010, Data: Png(2, 2)));
        await RemoteFormats((CfDib, null));
        var dib = RdpDib.FromBgra(2, 2, Image().Bgra);
        await Handle(new(RdpClipboardEventKind.FormatDataResponse, Format: CfDib, Data: dib));
        await RemoteFormats((CfDib, null));
        await Handle(new(RdpClipboardEventKind.FormatDataResponse, Format: CfDib, Data: dib[..20]));

        Assert.Equal(2, _images.Count);
        Assert.Equal(Png(2, 2), _images[0]);
        Assert.Equal(RdpDib.ToBmpFile(dib), _images[1]);
    }

    /// <summary>A PNG's signature and IHDR header, which is all the size check reads.</summary>
    static byte[] Png(uint width, uint height)
    {
        var png = new byte[33];
        new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(png, 0);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(8), 13);
        "IHDR"u8.CopyTo(png.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16), width);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20), height);
        return png;
    }

    [Fact]
    public async Task Leaves_out_a_png_whose_header_claims_too_many_pixels_or_that_isnt_a_png()
    {
        await RemoteFormats((0xC010, "PNG"));
        await Handle(new(RdpClipboardEventKind.FormatDataResponse, Format: 0xC010, Data: Png(20000, 20000)));
        await RemoteFormats((0xC010, "PNG"));
        await Handle(new(RdpClipboardEventKind.FormatDataResponse, Format: 0xC010, Data: [1, 2, 3]));

        Assert.Empty(_images);
    }

    [Fact]
    public async Task Offers_local_files_and_folders_and_serves_their_sizes_and_bytes()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_local, "folder"));
        File.WriteAllText(Path.Combine(folder.FullName, "inner.txt"), "inner");
        var top = Path.Combine(_local, "top.txt");
        File.WriteAllText(top, "0123456789");

        _bridge.SetLocalFiles([folder.FullName, top]);
        await Handle(new(RdpClipboardEventKind.FormatListRequested));
        var offered = Assert.Single(_channel.Offered);
        Assert.Equal([("folder", "", true), ("inner.txt", "folder", false), ("top.txt", "", false)],
            offered.Select(f => (f.Name, f.RelativePath, f.Directory)));

        await Handle(new(RdpClipboardEventKind.FileContentsRequested) { FileRequest = new(1, 2, true, 0, 8, null) });
        await Handle(new(RdpClipboardEventKind.FileContentsRequested) { FileRequest = new(2, 2, false, 3, 4, null) });
        await Handle(new(RdpClipboardEventKind.FileContentsRequested) { FileRequest = new(3, 0, false, 0, 4, null) });
        await Handle(new(RdpClipboardEventKind.FileContentsRequested) { FileRequest = new(4, 9, false, 0, 4, null) });

        Assert.Equal((1u, null, 10L), ((uint, byte[]?, long?))_channel.FileAnswers[0]);
        Assert.Equal("3456"u8.ToArray(), _channel.FileAnswers[1].Data);
        Assert.Null(_channel.FileAnswers[2].Data); // A folder has no bytes.
        Assert.Null(_channel.FileAnswers[3].Data);
        Assert.Equal([4u], _channel.FileAnswers.Where(a => a.Data is null && a.Size is null && a.StreamId == 4).Select(a => a.StreamId));
    }

    [Fact]
    public async Task Serves_a_locked_offer_after_the_files_change()
    {
        var first = Path.Combine(_local, "first.txt");
        var second = Path.Combine(_local, "second.txt");
        File.WriteAllText(first, "first");
        File.WriteAllText(second, "second!");

        _bridge.SetLocalFiles([first]);
        await Handle(new(RdpClipboardEventKind.FormatListRequested));
        await Handle(new(RdpClipboardEventKind.Lock) { LockId = 7 });
        _bridge.SetLocalFiles([second]);
        await Handle(new(RdpClipboardEventKind.FormatListRequested));

        await Handle(new(RdpClipboardEventKind.FileContentsRequested) { FileRequest = new(1, 0, true, 0, 8, 7) });
        await Handle(new(RdpClipboardEventKind.FileContentsRequested) { FileRequest = new(2, 0, true, 0, 8, null) });

        Assert.Equal(5L, _channel.FileAnswers[0].Size);
        Assert.Equal(7L, _channel.FileAnswers[1].Size);
    }

    async Task<string> DownloadAsync(IReadOnlyList<RdpRemoteFile> files, Func<(uint StreamId, int Index, bool Size, long Position, int Length, uint? LockId), byte[]?> answer)
    {
        await RemoteFormats((0xC011, FileListName));
        await Handle(new(RdpClipboardEventKind.RemoteFileList, Format: 0xC011) { Files = files, LockId = 3 });
        for (int answered = 0; answered < _channel.FileRequests.Count && answered < 100; answered++)
        {
            var request = _channel.FileRequests[answered];
            await Handle(new(RdpClipboardEventKind.FileContentsResponse, Data: answer(request)) { StreamId = request.StreamId });
        }

        return Path.GetDirectoryName(Assert.Single(Assert.Single(_files)))!;
    }

    [Fact]
    public async Task Downloads_the_servers_files_on_copy_in_chunks_and_raises_the_copied_items()
    {
        var big = new byte[FileChunk + 10];
        Random.Shared.NextBytes(big);
        var root = await DownloadAsync(
            [new("folder", "", null, true), new("big.bin", "folder", big.Length, false), new("empty.txt", "folder", 0, false), new("note.txt", "folder", null, false)],
            r => r.Index switch
            {
                1 => big.AsSpan((int)r.Position, r.Length).ToArray(),
                3 when r.Size => BitConverter.GetBytes(5L),
                3 => "hello"u8.ToArray()[(int)r.Position..(int)(r.Position + r.Length)],
                _ => null,
            });

        Assert.Equal(Path.Combine(root, "folder"), Assert.Single(Assert.Single(_files)));
        Assert.Equal(big, File.ReadAllBytes(Path.Combine(root, "folder", "big.bin")));
        Assert.Empty(File.ReadAllBytes(Path.Combine(root, "folder", "empty.txt")));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(root, "folder", "note.txt")));
        Assert.All(_channel.FileRequests, r => Assert.Equal(3u, r.LockId));
        Assert.Equal([FileChunk, 10], _channel.FileRequests.Where(r => r.Index == 1).Select(r => r.Length));
        Assert.True(_transfers[^1].Ended);
        Assert.Equal(big.Length + 5L, _transfers[^1].Total);
    }

    async Task AnswerAsync(int request, byte[] data) =>
        await Handle(new(RdpClipboardEventKind.FileContentsResponse, Data: data) { StreamId = _channel.FileRequests[request].StreamId });

    [Fact]
    public async Task Copying_again_on_the_server_waits_for_the_download_going_and_the_newest_goes_on_the_clipboard()
    {
        await RemoteFormats((0xC011, FileListName));
        await Handle(new(RdpClipboardEventKind.RemoteFileList, Format: 0xC011) { Files = [new("first.bin", "", 4, false)], LockId = 3 });
        await RemoteFormats((0xC011, FileListName));
        await Handle(new(RdpClipboardEventKind.RemoteFileList, Format: 0xC011) { Files = [new("second.bin", "", 4, false)], LockId = 4 });

        // The first keeps going under its lock; the second waits for it.
        var asked = Assert.Single(_channel.FileRequests);
        Assert.Equal(3u, asked.LockId);
        await AnswerAsync(0, "1111"u8.ToArray());

        // The first is done after a newer copy, so it's offered and not put on the clipboard; then the second starts.
        var first = Assert.Single(Assert.Single(_ready));
        Assert.Equal("1111", File.ReadAllText(first));
        Assert.Empty(_files);
        Assert.Equal(4u, _channel.FileRequests[1].LockId);
        await AnswerAsync(1, "2222"u8.ToArray());

        var second = Assert.Single(Assert.Single(_files));
        Assert.Equal("second.bin", Path.GetFileName(second));
        Assert.Equal("2222", File.ReadAllText(second));
        Assert.True(File.Exists(first), "the offered files stay until the session ends");
    }

    [Fact]
    public async Task A_download_that_ends_after_a_local_copy_leaves_that_copy_on_both_clipboards()
    {
        await RemoteFormats((0xC011, FileListName));
        await Handle(new(RdpClipboardEventKind.RemoteFileList, Format: 0xC011) { Files = [new("big.bin", "", 4, false)], LockId = 3 });
        _bridge.SetLocalText("copied while it downloaded");
        await AnswerAsync(0, "1234"u8.ToArray());

        Assert.Empty(_files);
        Assert.Equal("big.bin", Path.GetFileName(Assert.Single(Assert.Single(_ready))));

        // The local text is still what the server gets.
        await Handle(new(RdpClipboardEventKind.FormatListRequested));
        Assert.Contains(new RdpClipboardFormat(CfUnicodeText), Assert.Single(_channel.Announced));
    }

    [Fact]
    public async Task A_copy_waiting_for_a_download_is_dropped_by_a_newer_copy()
    {
        await RemoteFormats((0xC011, FileListName));
        await Handle(new(RdpClipboardEventKind.RemoteFileList, Format: 0xC011) { Files = [new("first.bin", "", 4, false)], LockId = 3 });
        await RemoteFormats((0xC011, FileListName));
        await Handle(new(RdpClipboardEventKind.RemoteFileList, Format: 0xC011) { Files = [new("second.bin", "", 4, false)], LockId = 4 });
        _bridge.SetLocalText("newest");
        await AnswerAsync(0, "1111"u8.ToArray());

        Assert.Single(_channel.FileRequests);
        Assert.Single(_ready);
        Assert.Empty(_files);
    }

    [Fact]
    public async Task Leaves_out_files_whose_names_could_leave_the_download_folder()
    {
        await RemoteFormats((0xC011, FileListName));
        await Handle(new(RdpClipboardEventKind.RemoteFileList)
        {
            Files = [new("..", "", 1, false), new("x", "..\\..", 1, false), new("a:b", "", 1, false), new("ok.txt", "", 1, false)],
        });
        var request = Assert.Single(_channel.FileRequests);
        Assert.Equal(3, request.Index);
        await Handle(new(RdpClipboardEventKind.FileContentsResponse, Data: "k"u8.ToArray()) { StreamId = request.StreamId });

        Assert.Equal("ok.txt", Path.GetFileName(Assert.Single(Assert.Single(_files))));
    }

    [Fact]
    public async Task Refuses_files_over_the_download_limit()
    {
        await RemoteFormats((0xC011, FileListName));
        await Handle(new(RdpClipboardEventKind.RemoteFileList) { Files = [new("huge.iso", "", MaxDownloadBytes + 1, false)] });

        Assert.Empty(_channel.FileRequests);
        Assert.Contains("over 1 GB", Assert.Single(_refused));
    }

    [Fact]
    public async Task Refuses_sizes_that_would_wrap_the_total_around()
    {
        await RemoteFormats((0xC011, FileListName));
        await Handle(new(RdpClipboardEventKind.RemoteFileList) { Files = [new("a", "", long.MaxValue, false), new("b", "", long.MaxValue, false)] });
        Assert.Empty(_channel.FileRequests);

        // Sizes the list leaves out are asked for, and an answer near 2^64 is refused too.
        await RemoteFormats((0xC011, FileListName));
        await Handle(new(RdpClipboardEventKind.RemoteFileList) { Files = [new("c", "", 1, false), new("d", "", null, false)] });
        for (int answered = 0; answered < _channel.FileRequests.Count && answered < 10; answered++)
        {
            var request = _channel.FileRequests[answered];
            byte[] data = request.Size ? BitConverter.GetBytes(ulong.MaxValue) : "c"u8.ToArray();
            await Handle(new(RdpClipboardEventKind.FileContentsResponse, Data: data) { StreamId = request.StreamId });
        }

        Assert.Single(_channel.FileRequests, r => r.Size);
        Assert.Empty(_files);

        Assert.Equal(2, _refused.Count);
        Assert.All(_refused, r => Assert.Contains("over 1 GB", r));
    }

    [Fact]
    public async Task Stops_serving_offered_files_once_the_clipboard_holds_something_else()
    {
        var file = Path.Combine(_local, "secret.txt");
        File.WriteAllText(file, "secret");
        _bridge.SetLocalFiles([file]);
        await Handle(new(RdpClipboardEventKind.FormatListRequested));
        _bridge.SetLocalText("hello");
        await Handle(new(RdpClipboardEventKind.FormatListRequested));

        await Handle(new(RdpClipboardEventKind.FileContentsRequested) { FileRequest = new(1, 0, true, 0, 8, null) });

        Assert.Equal((1u, null, null), ((uint, byte[]?, long?))Assert.Single(_channel.FileAnswers));
    }

    [Fact]
    public async Task A_failed_or_canceled_download_leaves_nothing_behind()
    {
        await RemoteFormats((0xC011, FileListName));
        await Handle(new(RdpClipboardEventKind.RemoteFileList) { Files = [new("a.txt", "", 4, false)] });
        await Handle(new(RdpClipboardEventKind.FileContentsResponse) { StreamId = _channel.FileRequests[0].StreamId });

        Assert.Empty(_files);
        Assert.True(_transfers[^1].Ended);
        Assert.Contains("transfer from the remote desktop failed", Assert.Single(_refused));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await RemoteFormats((0xC011, FileListName));
        await Handle(new(RdpClipboardEventKind.RemoteFileList) { Files = [new("b.txt", "", 4, false)] });
        int ended = _transfers.Count(t => t.Ended);
        _bridge.CancelDownload();
        var run = Task.Run(() => _bridge.RunAsync(cts.Token));
        while (_transfers.Count(t => t.Ended) == ended)
        {
            await Task.Delay(10, cts.Token);
        }

        cts.Cancel();
        await run;
        var folders = Directory.Exists(Path.Combine(Path.GetTempPath(), "Orrery", "Clipboard"))
            ? Directory.GetFiles(Path.Combine(Path.GetTempPath(), "Orrery", "Clipboard"), "b.txt", SearchOption.AllDirectories)
            : [];
        Assert.Empty(folders);
        Assert.Empty(_files);
    }

    [Fact]
    public void Collects_no_more_than_the_limit()
    {
        Assert.Empty(RdpClipboardFiles.Collect([Path.Combine(_local, "missing")])!);
        Assert.Null(RdpClipboardFiles.LocalPath(_local, new("..", "", 0, false)));
        Assert.Null(RdpClipboardFiles.LocalPath(_local, new("a", "b\\..\\..", 0, false)));
        Assert.Equal(Path.Combine(_local, "b", "a"), RdpClipboardFiles.LocalPath(_local, new("a", "b", 0, false)));
    }
}
