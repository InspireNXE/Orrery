using System.Net;
using System.Net.Sockets;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Orrery.App.Sessions;
using Orrery.Core.Connections;
using Orrery.Core.Security;
using Orrery.Core.Sessions;
using Orrery.Protocols.Ssh;
using Xunit;

namespace Orrery.App.Tests;

public class FileBrowserViewTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(10 * 1024 * 1024, "10 MB")]
    [InlineData(5L * 1024 * 1024 * 1024, "5.0 GB")]
    public void Sizes_read_in_powers_of_1024(long bytes, string expected) =>
        Assert.Equal(expected, FileBrowserView.FormatSize(bytes).Replace(',', '.'));

    [AvaloniaFact]
    public async Task Shows_why_the_connection_failed_instead_of_an_empty_listing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"orrery-known-hosts-{Guid.NewGuid():N}.json");
        var validator = new HostKeyValidator(new JsonCertificateTrustStore(path), _ => Task.FromResult(CertificateTrustDecision.Reject));
        var session = SftpSession.Start(new ConnectionTarget("127.0.0.1", ClosedPort(), "me", null), new SshLogin("me", "pw"), validator);
        var view = new FileBrowserView(session);
        var window = new Window { Content = view, Width = 900, Height = 500 };
        window.Show();
        try
        {
            var until = DateTime.UtcNow.AddSeconds(30);
            while (session.State != SessionState.Failed && DateTime.UtcNow < until)
            {
                await Task.Delay(50);
            }

            Dispatcher.UIThread.RunJobs();
            var texts = view.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
            Assert.Contains(texts, t => t?.StartsWith("Connection failed: ") == true);
            Assert.Empty(view.Items);
        }
        finally
        {
            window.Close();
            await session.DisposeAsync();
            File.Delete(path);
        }
    }

    static SftpItem Item(string name, bool folder = false) => new(name, "/srv/" + name, folder, false, 10, DateTime.Now, "rw-r--r--");

    static List<string?> Headers(IReadOnlyList<FileBrowserView.FileMenuEntry?> menu) => menu.Select(e => e?.Header).ToList();

    [Fact]
    public void The_right_click_menu_fits_what_is_selected()
    {
        Assert.Equal(["Download…", null, "Rename…", "Delete…", null, "Copy path", "Copy name"], Headers(FileBrowserView.MenuFor([Item("nginx.conf")], busy: false)));
        Assert.Equal(["Open", "Download…", null, "Rename…", "Delete…", null, "Copy path", "Copy name"], Headers(FileBrowserView.MenuFor([Item("assets", folder: true)], busy: false)));
        Assert.Equal(["Upload files…", "Upload a folder…", "New folder…", null, "Refresh", "Copy this folder's path"], Headers(FileBrowserView.MenuFor([], busy: false)));

        var several = FileBrowserView.MenuFor([Item("a"), Item("b"), Item("c")], busy: false);
        Assert.Equal(["Download 3 items…", null, "Rename…", "Delete 3 items…", null, "Copy paths"], Headers(several));
        Assert.False(several.Single(e => e?.Action == FileBrowserView.FileAction.Rename)!.Enabled);
    }

    [Fact]
    public void A_transfer_under_way_turns_off_downloads_and_uploads_in_the_menu()
    {
        Assert.False(FileBrowserView.MenuFor([Item("nginx.conf")], busy: true).Single(e => e?.Action == FileBrowserView.FileAction.Download)!.Enabled);
        Assert.All(FileBrowserView.MenuFor([], busy: true).Where(e => e?.Action is FileBrowserView.FileAction.UploadFiles or FileBrowserView.FileAction.UploadFolder),
            e => Assert.False(e!.Enabled));
        Assert.True(FileBrowserView.MenuFor([Item("nginx.conf")], busy: true).Single(e => e?.Action == FileBrowserView.FileAction.Delete)!.Enabled);
    }

    [Fact]
    public void Menu_shortcuts_are_gestures_a_menu_can_show()
    {
        foreach (var entry in new[] { FileBrowserView.MenuFor([], false), FileBrowserView.MenuFor([Item("a")], false), FileBrowserView.MenuFor([Item("a"), Item("b")], false) }
                     .SelectMany(m => m).OfType<FileBrowserView.FileMenuEntry>().Where(e => e.Shortcut is not null))
        {
            Avalonia.Input.KeyGesture.Parse(entry.Shortcut!.Replace("Cmd", "Meta"));
        }

        // A menu would show Enter as "Return", so Open and Download leave it off, as the connection tree's menu does.
        Assert.DoesNotContain(FileBrowserView.MenuFor([Item("a")], false), e => e?.Shortcut == "Enter");
    }

    [AvaloniaFact]
    public void A_file_tab_is_labeled_SFTP()
    {
        var tab = new SessionTab(new FakeSession(), new Border(), new SessionOrigin("box · files", Protocol.Ssh, null, () => Task.CompletedTask) { Files = true });
        var window = new Window { Content = tab.Header };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(tab.Header.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "SFTP");
        window.Close();
    }

    sealed class FakeSession : IConnectionSession
    {
        public ConnectionTarget Target { get; } = new("box", 22, "user", null);
        public SessionState State => SessionState.Connected;
        public event EventHandler<SessionStateChangedEventArgs>? StateChanged { add { } remove { } }
        public Task DisconnectAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
