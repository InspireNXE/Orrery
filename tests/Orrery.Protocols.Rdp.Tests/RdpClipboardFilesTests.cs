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
    readonly List<ClipboardTransfer> _transfers = [];
    readonly List<string> _refused = [];
    readonly string _local = Path.Combine(Path.GetTempPath(), "orrery-test-" + Guid.NewGuid().ToString("N"));

    public RdpClipboardFilesTests()
    {
        _bridge = new RdpClipboardBridge(_channel);
        _bridge.RemoteImageChanged += (_, image) => _images.Add(image);
        _bridge.RemoteFilesChanged += (_, paths) => _files.Add(paths);
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
        await Handle(new(RdpClipboardEventKind.FormatDataResponse, Format: 0xC010, Data: [1, 2, 3]));
        await RemoteFormats((CfDib, null));
        var dib = RdpDib.FromBgra(2, 2, Image().Bgra);
        await Handle(new(RdpClipboardEventKind.FormatDataResponse, Format: CfDib, Data: dib));
        await RemoteFormats((CfDib, null));
        await Handle(new(RdpClipboardEventKind.FormatDataResponse, Format: CfDib, Data: dib[..20]));

        Assert.Equal(2, _images.Count);
        Assert.Equal([1, 2, 3], _images[0]);
        Assert.Equal(RdpDib.ToBmpFile(dib), _images[1]);
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
