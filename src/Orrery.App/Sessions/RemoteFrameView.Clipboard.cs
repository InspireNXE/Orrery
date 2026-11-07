using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Orrery.App.Controls;
using Orrery.Core.Diagnostics;
using Orrery.Core.Sessions;

namespace Orrery.App.Sessions;

// Images and files on the clipboard, for sessions that share them through IClipboardSession (RDP off Windows).
public sealed partial class RemoteFrameView
{
    // The image and files last exchanged with the server, so a change isn't sent back where it came from.
    string? _lastImage;
    string? _lastFiles;

    // Shows while files the server copied are downloaded, with a way to stop it. Also offers files whose download ended
    // after something newer was copied, which aren't put on the clipboard over it.
    readonly Notice _transferNotice = new(NoticeKind.Info) { VerticalAlignment = VerticalAlignment.Center };
    Border? _transfer;
    Button? _transferCancel;
    StackPanel? _readyButtons;
    IReadOnlyList<string>? _ready;

    /// <summary>Replaces the write of an image to the local clipboard, for tests.</summary>
    internal Func<Bitmap, Task>? SetClipboardImage { get; set; }

    /// <summary>Replaces the write of files to the local clipboard, for tests.</summary>
    internal Func<IReadOnlyList<string>, Task>? SetClipboardFiles { get; set; }

    /// <summary>What the download strip says while it shows, for tests.</summary>
    internal string? TransferText => _transfer?.IsVisible == true ? _transferNotice.Text : null;

    void ShareFilesAndImages(IClipboardSession session)
    {
        var cancel = new Button { Content = "Cancel", VerticalAlignment = VerticalAlignment.Center };
        cancel.Click += (_, _) => session.CancelClipboardTransfer();
        var copy = new Button { Content = "Copy them", VerticalAlignment = VerticalAlignment.Center };
        copy.Classes.Add("accent");
        copy.Click += async (_, _) =>
        {
            var paths = _ready;
            HideReady();
            if (paths is not null)
            {
                await PutFilesOnClipboardAsync(paths);
            }
        };
        var dismiss = new Button { Content = "Dismiss", VerticalAlignment = VerticalAlignment.Center };
        dismiss.Click += (_, _) => HideReady();
        _transferCancel = cancel;
        _readyButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, IsVisible = false, Children = { copy, dismiss } };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 0), Children = { cancel, _readyButtons } };
        _transfer = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(16, 12),
            MaxWidth = 640,
            Padding = new Thickness(14, 8, 8, 8),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = Palette.Accent,
            Background = Dialogs.PanelBrush,
            BoxShadow = BoxShadows.Parse("0 4 16 0 #80000000"),
            IsVisible = false,
            Child = new DockPanel { Children = { Pane.Docked(buttons, Dock.Right), _transferNotice } },
        };
        Children.Add(_transfer);

        session.ClipboardTransferChanged += (_, transfer) => Dispatcher.UIThread.Post(() => ShowTransfer(transfer));
        session.RemoteClipboardImageChanged += (_, encoded) => Dispatcher.UIThread.Post(async () =>
        {
            Bitmap bitmap;
            try
            {
                bitmap = new Bitmap(new MemoryStream(encoded));
                _lastImage = ImageKey(ToClipboardImage(bitmap, png: false));
            }
            catch (Exception e)
            {
                // The server's image data is untrusted and may not decode.
                Log.For("RemoteDesktop").LogWarning("Couldn't read the remote desktop's clipboard image: {Message}", e.Message);
                return;
            }

            var write = SetClipboardImage ?? (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard ? b => clipboard.SetBitmapAsync(b) : null);
            await WriteClipboardAsync(write is null ? null : () => write(bitmap));
        });
        session.RemoteClipboardFilesChanged += (_, paths) => Dispatcher.UIThread.Post(async () => await PutFilesOnClipboardAsync(paths));
        session.RemoteClipboardFilesReady += (_, paths) => Dispatcher.UIThread.Post(() => ShowReady(paths));
    }

    async Task PutFilesOnClipboardAsync(IReadOnlyList<string> paths)
    {
        _lastFiles = FilesKey(paths);
        var write = SetClipboardFiles ?? (TopLevel.GetTopLevel(this) is { Clipboard: { } clipboard, StorageProvider: { } storage }
            ? async p => await clipboard.SetFilesAsync(await StorageItemsAsync(storage, p))
            : null);
        await WriteClipboardAsync(write is null ? null : () => write(paths));
    }

    /// <summary>Offers files whose download ended after something newer was copied, which stays on the clipboard.</summary>
    void ShowReady(IReadOnlyList<string> paths)
    {
        if (_transfer is null)
        {
            return;
        }

        _ready = paths;
        var items = paths.Count == 1 ? $"\"{Path.GetFileName(Path.TrimEndingDirectorySeparator(paths[0]))}\"" : $"{paths.Count:N0} items";
        _transferNotice.Text = $"{items} from the remote desktop finished copying after you copied something else, so they aren't on the clipboard.";
        _transferCancel!.IsVisible = false;
        _readyButtons!.IsVisible = true;
        _transfer.IsVisible = true;
        ScreenReader.Announce(this, _transferNotice.Text, "Orrery.ClipboardTransfer", ScreenReader.MostRecent);
    }

    void HideReady()
    {
        _ready = null;
        _transfer!.IsVisible = false;
    }

    static async Task WriteClipboardAsync(Func<Task>? write)
    {
        if (write is null)
        {
            return;
        }

        try
        {
            await write();
        }
        catch (Exception e) when (e is COMException or InvalidOperationException)
        {
            // Another app holds the clipboard open; the server's next copy tries again.
            Log.For("RemoteDesktop").LogWarning("Couldn't write the clipboard: {Message}", e.Message);
        }
    }

    static async Task<List<IStorageItem>> StorageItemsAsync(IStorageProvider storage, IReadOnlyList<string> paths)
    {
        var items = new List<IStorageItem>();
        foreach (var path in paths)
        {
            var uri = new Uri(path);
            IStorageItem? item = Directory.Exists(path) ? await storage.TryGetFolderFromPathAsync(uri) : await storage.TryGetFileFromPathAsync(uri);
            if (item is not null)
            {
                items.Add(item);
            }
        }

        return items;
    }

    void ShowTransfer(ClipboardTransfer transfer)
    {
        if (_transfer is null)
        {
            return;
        }

        if (transfer.Ended)
        {
            // Files offered stay offered until they're copied or dismissed.
            if (_ready is null)
            {
                _transfer.IsVisible = false;
            }

            return;
        }

        if (_ready is not null)
        {
            // A new download takes the strip over; the offered files stay in their folder until the session ends.
            _ready = null;
            _transfer.IsVisible = false;
        }

        _transferCancel!.IsVisible = true;
        _readyButtons!.IsVisible = false;
        var files = transfer.Files == 1 ? "1 file" : $"{transfer.Files:N0} files";
        _transferNotice.Text = $"Copying {files} from the remote desktop: {Size(transfer.Done)} of {Size(transfer.Total)}";
        if (!_transfer.IsVisible)
        {
            _transfer.IsVisible = true;
            ScreenReader.Announce(this, $"Copying {files} from the remote desktop", "Orrery.ClipboardTransfer", ScreenReader.MostRecent);
        }
    }

    static string Size(long bytes) => bytes switch
    {
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };

    /// <summary>
    /// Hands the local files to the server if the clipboard holds files that changed since the last exchange.
    /// </summary>
    /// <returns>Whether the clipboard holds files, changed or not, so nothing else on it is sent.</returns>
    async Task<bool> SendLocalFilesAsync(IClipboard clipboard, IClipboardSession session)
    {
        if (await clipboard.TryGetFilesAsync() is not { Length: > 0 } items)
        {
            return false;
        }

        var paths = items.Select(item => item.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count == 0)
        {
            return false;
        }

        var key = FilesKey(paths);
        if (key != _lastFiles)
        {
            _lastFiles = key;
            _lastClipboard = null;
            session.SendClipboardFiles(paths);
        }

        return true;
    }

    /// <summary>Hands the local image to the server if it changed since the last exchange.</summary>
    async Task SendLocalImageAsync(IClipboard clipboard, IClipboardSession session)
    {
        ClipboardImage image;
        try
        {
            using var bitmap = await clipboard.TryGetBitmapAsync();
            if (bitmap is null)
            {
                return;
            }

            image = ToClipboardImage(bitmap, png: true);
        }
        catch (Exception e) when (e is not (COMException or InvalidOperationException))
        {
            // Image data another app put on the clipboard that doesn't decode.
            Log.For("RemoteDesktop").LogWarning("Couldn't read the clipboard image: {Message}", e.Message);
            return;
        }

        var key = ImageKey(image);
        if (key != _lastImage)
        {
            _lastImage = key;
            _lastClipboard = null;
            session.SendClipboardImage(image);
        }
    }

    /// <summary>
    /// The files by their names and folders' names: the clipboard can give back another spelling of a path written
    /// to it, such as macOS's /private/var for /var, and downloads each go in a folder of their own.
    /// </summary>
    static string FilesKey(IEnumerable<string> paths) =>
        string.Join('\n', paths.Select(p => Path.TrimEndingDirectorySeparator(p)).Select(p => Path.Combine(Path.GetFileName(Path.GetDirectoryName(p)) ?? "", Path.GetFileName(p))));

    static string ImageKey(ClipboardImage image) => $"{image.Width}x{image.Height}:{Convert.ToHexString(SHA256.HashData(image.Bgra))}";

    /// <summary>The bitmap's pixels as BGRA, top row first and alpha not premultiplied, and as PNG when <paramref name="png"/>.</summary>
    internal static ClipboardImage ToClipboardImage(Bitmap bitmap, bool png)
    {
        var size = bitmap.PixelSize;
        using var pixels = new WriteableBitmap(size, bitmap.Dpi, PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        var bgra = new byte[size.Width * size.Height * 4];
        using (var frame = pixels.Lock())
        {
            bitmap.CopyPixels(frame);
            for (int row = 0; row < size.Height; row++)
            {
                Marshal.Copy(frame.Address + row * frame.RowBytes, bgra, row * size.Width * 4, size.Width * 4);
            }
        }

        byte[] encoded = [];
        if (png)
        {
            using var stream = new MemoryStream();
            bitmap.Save(stream, PngBitmapEncoderOptions.Default);
            encoded = stream.ToArray();
        }

        return new ClipboardImage(size.Width, size.Height, bgra, encoded);
    }
}
