using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Orrery.App.Connections;
using Orrery.App.Recording;
using Orrery.App.Sessions;
using Orrery.Core.Connections;
using Orrery.Core.Credentials;
using Orrery.Core.Recording;
using Orrery.Core.Sessions;
using Orrery.Core.Settings;
using Orrery.Core.Storage;
using Xunit;

namespace Orrery.App.Tests;

public sealed class RecordingUiTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));
    readonly DataLocation _location;
    readonly AppSettingsStore _settings;
    readonly KeychainCredentialStore _credentials;

    public RecordingUiTests()
    {
        Directory.CreateDirectory(_root);
        _location = new DataLocation(StorageMode.Portable, _root, Path.Combine(_root, "cache"), Path.Combine(_root, "logs"));
        _settings = AppSettingsStore.In(_location);
        _credentials = new KeychainCredentialStore(_location.FileIn("credentials.json"), new MemorySecrets());
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    sealed class Terminal : ITerminalSession
    {
        public ConnectionTarget Target { get; } = new("web.example", 22, "admin", null);
        public SessionState State => SessionState.Connected;
        public event EventHandler<SessionStateChangedEventArgs>? StateChanged { add { } remove { } }
        public event Action<byte[]>? DataReceived;
        public void Output(string text) => DataReceived?.Invoke(Encoding.UTF8.GetBytes(text));
        public void Send(ReadOnlySpan<byte> data) { }
        public void Resize(int columns, int rows) { }
        public Task DisconnectAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    static SessionTab NewTab(Terminal session) =>
        new(session, new TerminalView(session), new SessionOrigin("web-01", Protocol.Ssh, null, () => Task.CompletedTask));

    [AvaloniaFact]
    public async Task A_recording_tab_shows_a_dot_and_says_so_until_it_stops()
    {
        var service = new RecordingService(_location, _settings, _credentials);
        var session = new Terminal();
        var tab = NewTab(session);
        var window = new Window { Content = tab.Header };
        window.Show();

        var (recorder, problem) = await service.StartAsync(tab, window);
        Assert.Null(problem);
        tab.Recorder = recorder;
        var dot = tab.Header.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>().Single();
        Assert.True(dot.IsVisible);
        Assert.EndsWith(", recording", tab.Header.Label);
        Assert.NotNull(_settings.Load().RecordingPublicKey);

        session.Output("hello from the server\r\n");
        recorder!.Stop();
        await recorder.Completion;
        Dispatcher.UIThread.RunJobs();

        Assert.False(dot.IsVisible);
        Assert.DoesNotContain("recording", tab.Header.Label);
        var file = Assert.Single(Directory.GetFiles(service.Folder, "*.orrec"));
        Assert.Contains("web-01", Path.GetFileName(file));
    }

    [AvaloniaFact]
    public async Task Names_can_be_left_out_of_file_names()
    {
        _settings.Save(new AppSettings { RecordingNamesInFiles = false });
        var service = new RecordingService(_location, _settings, _credentials);
        var tab = NewTab(new Terminal());
        var window = new Window { Content = tab.Header };
        window.Show();

        var (recorder, _) = await service.StartAsync(tab, window);
        recorder!.Stop();
        await recorder.Completion;

        Assert.DoesNotContain("web-01", Path.GetFileName(Assert.Single(Directory.GetFiles(service.Folder))));
    }

    [Theory]
    [InlineData("web/01: \"prod\"", "web_01_ _prod_")]
    [InlineData("trailing dots...", "trailing dots")]
    public void File_names_hold_only_what_every_system_allows(string title, string expected) =>
        Assert.Equal(expected, RecordingService.SafeName(title));

    [AvaloniaFact]
    public async Task The_record_switch_is_saved_and_inherited()
    {
        var tree = ConnectionTree.Empty();
        var folder = new ConnectionFolder { Name = "Audited", Settings = new InheritableSettings { RecordSessions = true } };
        var entry = new ConnectionEntry { Name = "box", Host = "box.example", Settings = new InheritableSettings { Protocol = Protocol.Ssh } };
        tree.Add(folder, tree.Root);
        tree.Add(entry, folder);

        var panel = new PropertiesPanel(tree, _credentials);
        var window = new Window { Content = panel, Width = 400, Height = 900 };
        window.Show();
        await panel.ShowAsync(entry);
        Dispatcher.UIThread.RunJobs();

        var record = window.GetVisualDescendants().OfType<ToggleSwitch>().Single(c => c.Name == "RecordSessions");
        Assert.True(record.IsChecked);
        Assert.True(SettingsResolver.Resolve(entry).RecordSessions.Value);

        record.IsChecked = false;
        Assert.True(panel.TrySave());
        Assert.False(entry.Settings.RecordSessions);
        Assert.False(SettingsResolver.Resolve(entry).RecordSessions.Value);
    }

    [AvaloniaFact]
    public async Task The_player_plays_a_terminal_recording_and_finds_text_in_it()
    {
        var publicKey = await RecordingKeys.EnsureAsync(_credentials);
        var path = Path.Combine(_root, "rec.orrec");
        using (var writer = RecordingWriter.Create(path, RecordingKind.Terminal, publicKey))
        {
            writer.Append(RecordingEventType.Info, TimeSpan.Zero, RecordingFile.InfoPayload(new RecordingInfo("web-01", "SSH", DateTimeOffset.Now)));
            writer.Append(RecordingEventType.Resize, TimeSpan.Zero, RecordingFile.Pair(40, 10));
            writer.Append(RecordingEventType.Output, TimeSpan.FromSeconds(1), "first line\r\n"u8);
            writer.Append(RecordingEventType.Marker, TimeSpan.FromSeconds(2), "Reconnected"u8);
            writer.Append(RecordingEventType.Output, TimeSpan.FromSeconds(60), "\x1b[31mneedle\x1b[0m here\r\n"u8);
            writer.Complete(TimeSpan.FromSeconds(61));
        }

        var service = new RecordingService(_location, _settings, _credentials);
        var item = new RecordingItem(path, new FileInfo(path).Length, DateTime.UtcNow, RecordingKind.Terminal, null, null);
        var player = new PlayerWindow(service, item, (await RecordingKeys.PrivateKeyAsync(_credentials))!);
        player.Show();
        await WaitFor(() => player.GetVisualDescendants().OfType<TerminalView>().Any());
        Assert.Contains(path, service.InUse);

        // Skip idle is on: the minute's gap plays as 2 seconds, so 61 seconds play as 5.
        var timeline = player.GetVisualDescendants().OfType<Slider>().Single();
        Assert.InRange(timeline.Maximum, 4.9, 5.1);
        timeline.Value = timeline.Maximum;
        Dispatcher.UIThread.RunJobs();
        var terminal = player.GetVisualDescendants().OfType<TerminalView>().Single();
        Assert.Contains("first line", terminal.AllText());
        Assert.Contains("needle here", terminal.AllText());
        Assert.Equal((40, 10), terminal.GridSize);

        // Back to the start: replayed into a fresh terminal.
        timeline.Value = 0.5;
        Dispatcher.UIThread.RunJobs();
        terminal = player.GetVisualDescendants().OfType<TerminalView>().Single();
        Assert.DoesNotContain("first line", terminal.AllText());

        var find = player.GetVisualDescendants().OfType<TextBox>().Single(t => t.Watermark == "Find in output");
        find.Text = "needle";
        player.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Find"))
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        terminal = player.GetVisualDescendants().OfType<TerminalView>().Single();
        Assert.Contains("needle", terminal.AllText());

        player.Close();
        Assert.DoesNotContain(path, service.InUse);
    }

    [AvaloniaFact]
    public async Task The_player_draws_a_screen_recording_and_says_when_it_ended_early()
    {
        var publicKey = await RecordingKeys.EnsureAsync(_credentials);
        var path = Path.Combine(_root, "screen.orrec");
        var writer = RecordingWriter.Create(path, RecordingKind.Screen, publicKey);
        var pixels = new byte[16 * 8 * 3];
        Array.Fill(pixels, (byte)200);
        writer.Append(RecordingEventType.Resize, TimeSpan.Zero, RecordingFile.Pair(16, 8));
        writer.Append(RecordingEventType.KeyFrame, TimeSpan.Zero, ScreenFrames.EncodeKeyFrame(pixels, 16, 8));
        writer.Flush();
        writer.Dispose(); // never completed, as if Orrery had stopped

        var service = new RecordingService(_location, _settings, _credentials);
        var item = new RecordingItem(path, new FileInfo(path).Length, DateTime.UtcNow, RecordingKind.Screen, null, null);
        var player = new PlayerWindow(service, item, (await RecordingKeys.PrivateKeyAsync(_credentials))!);
        player.Show();
        await WaitFor(() => player.GetVisualDescendants().OfType<Image>().Any(i => i.Source is not null));

        var image = player.GetVisualDescendants().OfType<Image>().Single();
        Assert.Equal(new Avalonia.PixelSize(16, 8), ((Avalonia.Media.Imaging.Bitmap)image.Source!).PixelSize);
        Assert.Contains(player.GetVisualDescendants().OfType<TextBlock>(), t => t.IsVisible && t.Text?.Contains("ended without being closed") == true);
        player.Close();
    }

    static async Task WaitFor(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Assert.True(condition());
    }
}
