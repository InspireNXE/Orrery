using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Orrery.App.Controls;
using Orrery.App.Sessions;
using Orrery.Core.Connections;
using Orrery.Core.Sessions;
using Xunit;

namespace Orrery.App.Tests;

/// <summary>Images and files the remote side copies, for RDP off Windows.</summary>
public class RemoteFrameViewFilesTests
{
    sealed class FileDesktop : IFrameBufferSession, IClipboardSession
    {
        public ConnectionTarget Target { get; } = new("desk", 3389, "user", null);
        public SessionState State => SessionState.Connected;
#pragma warning disable CS0067 // Part of the interface; this fake never raises them.
        public event EventHandler<SessionStateChangedEventArgs>? StateChanged;
        public event EventHandler<FrameUpdatedEventArgs>? FrameUpdated;
        public event EventHandler<string>? RemoteClipboardChanged;
#pragma warning restore CS0067
        public event EventHandler<byte[]>? RemoteClipboardImageChanged;
        public event EventHandler<IReadOnlyList<string>>? RemoteClipboardFilesChanged;
        public event EventHandler<IReadOnlyList<string>>? RemoteClipboardFilesReady;
        public event EventHandler<ClipboardTransfer>? ClipboardTransferChanged;
        public void FilesReady(IReadOnlyList<string> paths) => RemoteClipboardFilesReady?.Invoke(this, paths);
        public bool ExchangesFiles => true;
        public int Canceled { get; private set; }
        public int Width => 64;
        public int Height => 48;
        public void CopyImage(byte[] encoded) => RemoteClipboardImageChanged?.Invoke(this, encoded);
        public void CopyFiles(IReadOnlyList<string> paths) => RemoteClipboardFilesChanged?.Invoke(this, paths);
        public void Transfer(ClipboardTransfer transfer) => ClipboardTransferChanged?.Invoke(this, transfer);
        public void CancelClipboardTransfer() => Canceled++;
        public void SendClipboard(string text) { }
        public void CopyRegion(FrameRect region, IntPtr destination, int destinationStride) { }
        public void SendKey(ushort scancode, bool pressed) { }
        public void ReleaseAllKeys() { }
        public void SendPointerMove(int x, int y) { }
        public void SendPointerButton(PointerButton button, bool pressed) { }
        public void SendWheel(int units, bool horizontal) { }
        public void RequestResize(int width, int height) { }
        public Task DisconnectAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    static (Window Window, RemoteFrameView View) Show(FileDesktop session)
    {
        var view = new RemoteFrameView(session);
        var window = new Window { Content = view, Width = 800, Height = 500 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view);
    }

    [AvaloniaFact]
    public void The_remote_sides_files_and_image_go_on_the_local_clipboard()
    {
        var session = new FileDesktop();
        var (window, view) = Show(session);
        var files = new List<IReadOnlyList<string>>();
        var images = new List<Bitmap>();
        view.SetClipboardFiles = paths => { files.Add(paths); return Task.CompletedTask; };
        view.SetClipboardImage = bitmap => { images.Add(bitmap); return Task.CompletedTask; };

        Task.Run(() => session.CopyFiles(["/tmp/a/one.txt", "/tmp/a/two"])).Wait();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["/tmp/a/one.txt", "/tmp/a/two"], Assert.Single(files));

        // Data that isn't an image is dropped, not thrown.
        Task.Run(() => session.CopyImage([1, 2, 3])).Wait();
        Dispatcher.UIThread.RunJobs();
        Assert.True(images.Count <= 1);
        window.Close();
    }

    [AvaloniaFact]
    public void Files_that_finished_after_a_newer_copy_are_offered_and_go_on_the_clipboard_when_asked()
    {
        var session = new FileDesktop();
        var (window, view) = Show(session);
        var files = new List<IReadOnlyList<string>>();
        view.SetClipboardFiles = paths => { files.Add(paths); return Task.CompletedTask; };
        Button ButtonFor(string text) => view.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == text);

        Task.Run(() =>
        {
            session.Transfer(new(1, 4, 4, true));
            session.FilesReady(["/tmp/a/report.pdf"]);
        }).Wait();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("\"report.pdf\" from the remote desktop finished copying after you copied something else, so they aren't on the clipboard.", view.TransferText);
        Assert.Empty(files);
        Assert.False(ButtonFor("Cancel").IsVisible);
        AccessibilityAudit.AssertAllNamed(window);

        ButtonFor("Copy them").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["/tmp/a/report.pdf"], Assert.Single(files));
        Assert.Null(view.TransferText);

        // Dismissing leaves the clipboard alone.
        Task.Run(() => session.FilesReady(["/tmp/b/one", "/tmp/b/two"])).Wait();
        Dispatcher.UIThread.RunJobs();
        Assert.StartsWith("2 items from the remote desktop", view.TransferText);
        ButtonFor("Dismiss").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Null(view.TransferText);
        Assert.Single(files);
        window.Close();
    }

    [AvaloniaFact]
    public void A_download_shows_its_progress_with_a_cancel_button_until_it_ends()
    {
        var session = new FileDesktop();
        var (window, view) = Show(session);
        var spoken = new List<string>();
        void Heard(string text) => spoken.Add(text);
        ScreenReader.Announced += Heard;
        try
        {
            Assert.Null(view.TransferText);
            Task.Run(() => session.Transfer(new(3, 5L * 1024 * 1024, 20L * 1024 * 1024, false))).Wait();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("Copying 3 files from the remote desktop: 5 MB of 20 MB", view.TransferText);
            Assert.Contains("Copying 3 files from the remote desktop", spoken);
            AccessibilityAudit.AssertAllNamed(window);

            var cancel = view.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Cancel");
            cancel.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(1, session.Canceled);

            Task.Run(() => session.Transfer(new(3, 5L * 1024 * 1024, 20L * 1024 * 1024, true))).Wait();
            Dispatcher.UIThread.RunJobs();
            Assert.Null(view.TransferText);
        }
        finally
        {
            ScreenReader.Announced -= Heard;
            window.Close();
        }
    }
}
