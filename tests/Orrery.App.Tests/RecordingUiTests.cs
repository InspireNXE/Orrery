using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Orrery.App.Controls;
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

    sealed class Tap : IAudioTap
    {
        public void SetEnabled(bool enabled) => Enabled = enabled;
        public bool Enabled { get; private set; }
        public AudioChunk? TryTake() => null;
        public bool Muted { get; set; }
        public TimeSpan? SoundAge { get; set; }
    }

    /// <summary>A remote desktop that plays sound here.</summary>
    sealed class SoundDesktop : IFrameBufferSession, IAudioSession
    {
        public Tap Tap { get; } = new();
        public IAudioTap? AudioTap => Tap;
        public ConnectionTarget Target { get; } = new("desk.example", 3389, "user", null);
        public SessionState State => SessionState.Connected;
        public event EventHandler<SessionStateChangedEventArgs>? StateChanged { add { } remove { } }
        public event EventHandler<FrameUpdatedEventArgs>? FrameUpdated { add { } remove { } }
        public int Width => 64;
        public int Height => 48;
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

    [AvaloniaFact]
    public void A_tab_playing_sound_shows_a_speaker_that_mutes_it_and_stays_while_muted()
    {
        var session = new SoundDesktop();
        var tab = new SessionTab(session, new RemoteFrameView(session), new SessionOrigin("desk", Protocol.Rdp, null, () => Task.CompletedTask));
        var window = new Window { Content = tab.Header };
        window.Show();
        StrokeIcon Icon() { window.UpdateLayout(); return Assert.IsType<StrokeIcon>(Speaker().Content); }
        Button Speaker() => tab.Header.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b)?.Contains("mute", StringComparison.OrdinalIgnoreCase) == true);

        Assert.True(tab.CanMute);
        tab.ShowSound();
        Assert.False(Speaker().IsVisible); // silent: no speaker

        session.Tap.SoundAge = TimeSpan.FromMilliseconds(200);
        tab.ShowSound();
        Assert.True(Speaker().IsVisible);
        Assert.Equal("Mute desk", AutomationProperties.GetName(Speaker()));
        Assert.NotNull(Icon().Foreground); // unmuted, the speaker still draws

        Speaker().RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.True(session.Tap.Muted);
        Assert.True(tab.IsMuted);

        session.Tap.SoundAge = TimeSpan.FromMinutes(1);
        tab.ShowSound();
        Assert.True(Speaker().IsVisible); // muted and quiet: still there, to unmute
        Assert.Equal("Unmute desk", AutomationProperties.GetName(Speaker()));

        tab.IsMuted = false;
        Assert.False(session.Tap.Muted);
        session.Tap.SoundAge = TimeSpan.FromMilliseconds(200);
        tab.ShowSound();
        Assert.True(Speaker().IsVisible);
        Assert.NotNull(Icon().Foreground); // back from muted, the speaker draws again
        session.Tap.SoundAge = TimeSpan.FromMinutes(1);
        tab.ShowSound();
        Assert.False(Speaker().IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_toolbar_offers_to_record_and_while_recording_a_pill_changes_the_frame_rate_and_sound()
    {
        var session = new SoundDesktop();
        var tab = new SessionTab(session, new RemoteFrameView(session), new SessionOrigin("desk", Protocol.Rdp, null, () => Task.CompletedTask));
        bool started = false;
        var record = Assert.IsType<Button>(RecordingControl.For(tab, () => started = true, () => { }, () => { }));
        Assert.Equal("Start recording", AutomationProperties.GetName(record));
        record.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.True(started);

        var publicKey = await RecordingKeys.EnsureAsync(_credentials);
        var recorder = new ScreenRecorder(RecordingWriter.Create(Path.Combine(_root, "desk.orrec"), RecordingKind.Screen, publicKey),
            new RecordingInfo("desk", "RDP", DateTimeOffset.Now), 2, long.MaxValue, recordSound: false);
        recorder.Attach(session);
        tab.Recorder = recorder;
        bool stopped = false;
        var pill = Assert.IsType<Button>(RecordingControl.For(tab, () => { }, () => stopped = true, () => { }));
        Assert.Equal("Recording", AutomationProperties.GetName(pill));
        var window = new Window { Content = pill, Width = 600, Height = 500 };
        window.Show();
        pill.Flyout!.ShowAt(pill);
        Dispatcher.UIThread.RunJobs();
        var panel = (Control)((Flyout)pill.Flyout).Content!;

        panel.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "At most 10 frames a second")
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(10, recorder.FramesPerSecond);

        var sound = panel.GetVisualDescendants().OfType<ToggleSwitch>().Single(t => AutomationProperties.GetName(t) == "Record sound");
        Assert.False(sound.IsChecked);
        sound.IsChecked = true;
        Assert.True(recorder.RecordsSound);
        Assert.True(session.Tap.Enabled);

        panel.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Stop recording"))
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.True(stopped);
        recorder.Stop();
        await recorder.Completion;
        window.Close();
    }

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
        var player = new PlayerView(service, item, (await RecordingKeys.PrivateKeyAsync(_credentials))!);
        var window = new Window { Content = player, Width = 1000, Height = 720 };
        window.Show();
        await WaitFor(() => player.GetVisualDescendants().OfType<TerminalView>().Any());
        Assert.Contains(path, service.InUse);
        Assert.Equal("web-01", player.Heading);
        Assert.Contains(player.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "SSH");
        Assert.Contains(player.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("1 min 1 s") == true);

        // Skip idle is on: the minute's gap plays as 2 seconds, so 61 seconds play as 5.
        var timeline = player.GetVisualDescendants().OfType<Slider>().Single(s => AutomationProperties.GetName(s) == "Position");
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

        var find = player.GetVisualDescendants().OfType<TextBox>().Single(t => t.PlaceholderText == "Find in output");
        find.Text = "needle";
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(player.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "1 found");
        player.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Next match")
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        terminal = player.GetVisualDescendants().OfType<TerminalView>().Single();
        Assert.Contains("needle", terminal.AllText());

        player.Dispose();
        Assert.DoesNotContain(path, service.InUse);
        window.Close();
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
        var player = new PlayerView(service, item, (await RecordingKeys.PrivateKeyAsync(_credentials))!);
        var window = new Window { Content = player, Width = 1000, Height = 720 };
        window.Show();
        await WaitFor(() => player.GetVisualDescendants().OfType<Image>().Any(i => i.Source is not null));

        var image = player.GetVisualDescendants().OfType<Image>().Single();
        Assert.Equal(new Avalonia.PixelSize(16, 8), ((Avalonia.Media.Imaging.Bitmap)image.Source!).PixelSize);
        Assert.Contains(player.GetVisualDescendants().OfType<TextBlock>(), t => t.IsVisible && t.Text?.Contains("ended without being closed") == true);
        player.Dispose();
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_screen_recording_with_nothing_in_it_says_so_rather_than_playing()
    {
        // Only the header was written, as when Orrery stops before the first flush.
        var publicKey = await RecordingKeys.EnsureAsync(_credentials);
        var path = Path.Combine(_root, "empty.orrec");
        RecordingWriter.Create(path, RecordingKind.Screen, publicKey).Dispose();

        var service = new RecordingService(_location, _settings, _credentials);
        var item = new RecordingItem(path, new FileInfo(path).Length, DateTime.UtcNow, RecordingKind.Screen, null, null);
        var player = new PlayerView(service, item, (await RecordingKeys.PrivateKeyAsync(_credentials))!);
        var window = new Window { Content = player, Width = 1000, Height = 720 };
        window.Show();
        await WaitFor(() => player.GetVisualDescendants().OfType<Notice>().Any(n => n.IsVisible));

        Press(player, Key.Space);
        Press(player, Key.End);
        Press(player, Key.Right, KeyModifiers.Shift);
        Assert.Equal("Play", AutomationProperties.GetName(PlayButton(player)));
        player.Dispose();
        window.Close();
    }

    [Fact]
    public async Task A_canceled_avi_export_leaves_no_file_behind()
    {
        var publicKey = await RecordingKeys.EnsureAsync(_credentials);
        var path = Path.Combine(_root, "long.orrec");
        var pixels = new byte[16 * 8 * 3];
        using (var writer = RecordingWriter.Create(path, RecordingKind.Screen, publicKey))
        {
            writer.Append(RecordingEventType.Resize, TimeSpan.Zero, RecordingFile.Pair(16, 8));
            writer.Append(RecordingEventType.KeyFrame, TimeSpan.Zero, ScreenFrames.EncodeKeyFrame(pixels, 16, 8));
            writer.Complete(TimeSpan.FromSeconds(4));
        }

        // Canceled partway, once the first part is open and being written.
        var output = Path.Combine(_root, "out.avi");
        var key = (await RecordingKeys.PrivateKeyAsync(_credentials))!;
        using var cancel = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => RecordingExport.ExportAvi(path, key, output, 25, skipIdle: false, _ => cancel.Cancel(), cancel.Token));
        Assert.False(File.Exists(output));
    }

    [AvaloniaTheory]
    [InlineData(2, 0.35, 0.55)] // twice as fast: about half as much sound, at the same pitch
    [InlineData(8, 0, 0)] // too fast to follow: none
    public async Task Sound_plays_stretched_to_the_speed(int speedSetting, double least, double most)
    {
        var publicKey = await RecordingKeys.EnsureAsync(_credentials);
        var path = Path.Combine(_root, "sound.orrec");
        var tone = new byte[4410 * 2 * 2];
        using (var writer = RecordingWriter.Create(path, RecordingKind.Screen, publicKey))
        {
            writer.Append(RecordingEventType.Resize, TimeSpan.Zero, RecordingFile.Pair(16, 8));
            writer.Append(RecordingEventType.KeyFrame, TimeSpan.Zero, ScreenFrames.EncodeKeyFrame(new byte[16 * 8 * 3], 16, 8));
            for (int i = 0; i < 20; i++)
            {
                writer.Append(RecordingEventType.Audio, TimeSpan.FromMilliseconds(100 * i), RecordingFile.AudioPayload(44100, 2, tone));
            }

            writer.Complete(TimeSpan.FromSeconds(2));
        }

        var output = new HeardOutput();
        var open = PlayerView.OpenAudio;
        PlayerView.OpenAudio = (_, _) => output;
        try
        {
            var service = new RecordingService(_location, _settings, _credentials);
            var item = new RecordingItem(path, new FileInfo(path).Length, DateTime.UtcNow, RecordingKind.Screen, null, null);
            var player = new PlayerView(service, item, (await RecordingKeys.PrivateKeyAsync(_credentials))!);
            var window = new Window { Content = player, Width = 1000, Height = 720 };
            window.Show();
            var speed = player.GetVisualDescendants().OfType<ComboBox>().Single(c => AutomationProperties.GetName(c) == "Playback speed");
            speed.SelectedItem = speed.Items.OfType<ComboBoxItem>().Single(i => (double)i.Tag! == speedSetting);
            var play = player.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) is "Play" or "Pause");
            await WaitFor(() => AutomationProperties.GetName(play) == "Pause"); // playing
            await WaitFor(() => AutomationProperties.GetName(play) == "Play again"); // played to the end
            lock (output.Heard)
            {
                Assert.InRange(output.Heard.Count, least * 20 * tone.Length, most * 20 * tone.Length);
            }

            player.Dispose();
            window.Close();
        }
        finally
        {
            PlayerView.OpenAudio = open;
        }
    }

    sealed class HeardOutput : IAudioOutput
    {
        public List<byte> Heard { get; } = [];
        public int Write(ReadOnlySpan<byte> pcm)
        {
            lock (Heard)
            {
                Heard.AddRange(pcm.ToArray());
            }

            return pcm.Length;
        }

        public TimeSpan Buffered => TimeSpan.Zero;
        public void Clear() { }
        public void SetPaused(bool paused) { }
        public void Dispose() { }
    }

    [AvaloniaFact]
    public async Task A_screen_recording_with_sound_plays_it_and_shows_a_volume_control()
    {
        var publicKey = await RecordingKeys.EnsureAsync(_credentials);
        var path = Path.Combine(_root, "sound.orrec");
        var pixels = new byte[16 * 8 * 3];
        var tone = new byte[4410 * 2 * 2];
        for (int i = 0; i < tone.Length; i += 2)
        {
            tone[i + 1] = 0x20; // 8192, loud enough to hear the volume take effect
        }

        using (var writer = RecordingWriter.Create(path, RecordingKind.Screen, publicKey))
        {
            writer.Append(RecordingEventType.Resize, TimeSpan.Zero, RecordingFile.Pair(16, 8));
            writer.Append(RecordingEventType.KeyFrame, TimeSpan.Zero, ScreenFrames.EncodeKeyFrame(pixels, 16, 8));
            for (int i = 0; i < 10; i++)
            {
                writer.Append(RecordingEventType.Audio, TimeSpan.FromMilliseconds(100 * i), RecordingFile.AudioPayload(44100, 2, tone));
            }

            writer.Complete(TimeSpan.FromSeconds(1));
        }

        var output = new HeardOutput();
        var open = PlayerView.OpenAudio;
        PlayerView.OpenAudio = (_, _) => output;
        try
        {
            var service = new RecordingService(_location, _settings, _credentials);
            var item = new RecordingItem(path, new FileInfo(path).Length, DateTime.UtcNow, RecordingKind.Screen, null, null);
            var player = new PlayerView(service, item, (await RecordingKeys.PrivateKeyAsync(_credentials))!);
            var window = new Window { Content = player, Width = 1000, Height = 720 };
            window.Show();
            var volume = player.GetVisualDescendants().OfType<Slider>().Single(s => AutomationProperties.GetName(s) == "Volume");
            await WaitFor(() => volume.IsEffectivelyVisible);
            volume.Value = 100;
            await WaitFor(() => { lock (output.Heard) return output.Heard.Count > 0; });
            lock (output.Heard)
            {
                Assert.Equal(0x20, output.Heard[1]); // played as recorded at full volume
            }

            player.Dispose();
            window.Close();
        }
        finally
        {
            PlayerView.OpenAudio = open;
        }
    }

    [Fact]
    public void The_volume_scales_samples_on_a_curve_and_full_volume_leaves_them_alone()
    {
        byte[] pcm = [0x00, 0x40, 0x00, 0xC0]; // 16384 and -16384
        Assert.Equal(pcm, PlayerView.Louder(pcm, 1).ToArray());
        Assert.Equal(new byte[] { 0x00, 0x10, 0x00, 0xF0 }, PlayerView.Louder(pcm, 0.5).ToArray()); // a quarter as loud
        Assert.All(PlayerView.Louder(pcm, 0).ToArray(), b => Assert.Equal(0, b));
    }

    [AvaloniaFact]
    public async Task The_side_panel_lists_recordings_newest_first_and_filters_them()
    {
        var publicKey = await RecordingKeys.EnsureAsync(_credentials);
        var service = new RecordingService(_location, _settings, _credentials);
        Directory.CreateDirectory(service.Folder);
        foreach (var (name, protocol, hoursAgo) in new[] { ("web-01", "SSH", 3), ("db-02", "RDP", 1) })
        {
            var path = Path.Combine(service.Folder, $"{name}.orrec");
            using (var writer = RecordingWriter.Create(path, protocol == "RDP" ? RecordingKind.Screen : RecordingKind.Terminal, publicKey))
            {
                writer.Append(RecordingEventType.Info, TimeSpan.Zero, RecordingFile.InfoPayload(new RecordingInfo(name, protocol, DateTimeOffset.Now.AddHours(-hoursAgo))));
                writer.Complete(TimeSpan.FromMinutes(5));
            }

            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-hoursAgo).AddMinutes(5));
        }

        var panel = new RecordingsPanel(service);
        var window = new Window { Content = panel, Width = 300, Height = 500 };
        window.Show();
        panel.Refresh();
        var list = panel.GetVisualDescendants().OfType<ListBox>().Single();
        await WaitFor(() => list.ItemCount == 2);

        // Newest first, named from inside the recording, with how long it ran.
        var items = list.Items.Cast<RecordingItem>().ToList();
        Assert.Equal(["db-02", "web-01"], items.Select(i => i.Title));
        Assert.InRange(items[0].Length!.Value.TotalSeconds, 298, 302);
        Assert.Contains(panel.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.StartsWith("2 recordings · ") == true && t.Text.EndsWith(" KB"));

        panel.Filter = "web";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["web-01"], list.Items.Cast<RecordingItem>().Select(i => i.Title));
        panel.Filter = "nothing like it";
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(panel.GetVisualDescendants().OfType<TextBlock>(), t => t.IsVisible && t.Text?.StartsWith("No recordings match") == true);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_long_recording_name_is_cut_short_before_its_badge()
    {
        var publicKey = await RecordingKeys.EnsureAsync(_credentials);
        var service = new RecordingService(_location, _settings, _credentials);
        Directory.CreateDirectory(service.Folder);
        const string name = "2026-11-02 23.15.20 inxe-vmh-01 - management console";
        var path = Path.Combine(service.Folder, "long.orrec");
        using (var writer = RecordingWriter.Create(path, RecordingKind.Screen, publicKey))
        {
            writer.Append(RecordingEventType.Info, TimeSpan.Zero, RecordingFile.InfoPayload(new RecordingInfo(name, "Screen", DateTimeOffset.Now)));
            writer.Complete(TimeSpan.FromMinutes(1));
        }

        var panel = new RecordingsPanel(service);
        var window = new Window { Content = panel, Width = 260, Height = 400 };
        window.Show();
        panel.Refresh();
        var list = panel.GetVisualDescendants().OfType<ListBox>().Single();
        await WaitFor(() => list.ItemCount == 1);
        Dispatcher.UIThread.RunJobs();

        var title = list.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == name);
        var badge = list.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "SCREEN");
        double titleRight = title.TranslatePoint(new Point(title.Bounds.Width, 0), window)!.Value.X;
        double badgeLeft = badge.TranslatePoint(new Point(0, 0), window)!.Value.X;
        Assert.True(titleRight <= badgeLeft, $"The name ends at {titleRight} but the badge starts at {badgeLeft}.");
        Assert.True(titleRight <= window.Width);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Choosing_a_recording_plays_it_in_the_main_area_and_deleting_it_closes_the_player()
    {
        var publicKey = await RecordingKeys.EnsureAsync(_credentials);
        var service = new RecordingService(_location, _settings, _credentials);
        Directory.CreateDirectory(service.Folder);
        var path = Path.Combine(service.Folder, "web-01.orrec");
        using (var writer = RecordingWriter.Create(path, RecordingKind.Terminal, publicKey))
        {
            writer.Append(RecordingEventType.Info, TimeSpan.Zero, RecordingFile.InfoPayload(new RecordingInfo("web-01", "SSH", DateTimeOffset.Now)));
            writer.Append(RecordingEventType.Output, TimeSpan.FromSeconds(1), "hello\r\n"u8);
            writer.Complete(TimeSpan.FromSeconds(2));
        }

        var panel = new RecordingsPanel(service);
        var window = new Window { Content = new DockPanel { Children = { Docked(panel), panel.Stage } }, Width = 1200, Height = 700 };
        window.Show();
        Assert.Contains(panel.Stage.GetVisualDescendants().OfType<TextBlock>(), t => t.IsVisible && t.Text == "Choose a recording to play");

        panel.Refresh();
        var list = panel.GetVisualDescendants().OfType<ListBox>().Single();
        await WaitFor(() => list.ItemCount == 1);
        list.SelectedIndex = 0;
        list.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        await WaitFor(() => panel.Stage.Player?.GetVisualDescendants().OfType<TerminalView>().Any() == true);
        Assert.Equal("web-01", panel.Stage.Player!.Heading);
        Assert.Contains(path, service.InUse);

        // Deleting the one playing lets go of it first, so the file can go.
        panel.Stage.Close(path);
        Assert.Null(panel.Stage.Player);
        Assert.DoesNotContain(path, service.InUse);
        Assert.Contains(panel.Stage.GetVisualDescendants().OfType<TextBlock>(), t => t.IsVisible && t.Text == "Choose a recording to play");
        window.Close();
    }

    static Control Docked(Control control)
    {
        DockPanel.SetDock(control, Dock.Left);
        control.Width = 280;
        return control;
    }

    [Fact]
    public void Export_offers_windows_codecs_first_and_ffmpeg_only_for_the_rest()
    {
        var system = new[] { new SystemCodec("H.264", Guid.NewGuid(), false), new SystemCodec("AV1", Guid.NewGuid(), true) };
        var ffmpeg = new FfmpegInfo("ffmpeg", "ffmpeg version 7", [new FfmpegCodec("H.264", "libx264"), new FfmpegCodec("H.265", "libx265")]);

        var routes = Mp4Route.All(system, ffmpeg);

        Assert.Equal(["MP4, H.264", "MP4, AV1 (graphics card)", "MP4, H.265 (through ffmpeg)"], routes.Select(r => r.Label));
        Assert.Empty(Mp4Route.All([], null));
    }

    [AvaloniaFact]
    public void Export_links_to_ffmpeg_only_when_it_can_offer_no_mp4()
    {
        static bool Links(ExportChoiceWindow window)
        {
            window.Show();
            var links = window.GetVisualDescendants().OfType<Button>().Any(b => Equals(b.Content, "Get ffmpeg"));
            window.Close();
            return links;
        }

        Assert.True(Links(new ExportChoiceWindow([], hasFfmpeg: false)));
        Assert.False(Links(new ExportChoiceWindow(Mp4Route.All([new SystemCodec("H.264", Guid.NewGuid(), false)], null), hasFfmpeg: false)));
        Assert.False(Links(new ExportChoiceWindow([], hasFfmpeg: true)));
    }

    async Task<(PlayerView Player, Window Window, string Path)> OpenTerminalRecording(string name, double width = 1000)
    {
        var publicKey = await RecordingKeys.EnsureAsync(_credentials);
        var path = Path.Combine(_root, $"{Guid.NewGuid():N}.orrec");
        using (var writer = RecordingWriter.Create(path, RecordingKind.Terminal, publicKey))
        {
            writer.Append(RecordingEventType.Info, TimeSpan.Zero, RecordingFile.InfoPayload(new RecordingInfo(name, "SSH", DateTimeOffset.Now)));
            writer.Append(RecordingEventType.Output, TimeSpan.Zero, "deploy started\r\n"u8);
            writer.Append(RecordingEventType.Marker, TimeSpan.FromMilliseconds(500), "Reconnected"u8);
            writer.Append(RecordingEventType.Output, TimeSpan.FromSeconds(1), "deploy done\r\n"u8);
            writer.Complete(TimeSpan.FromSeconds(1));
        }

        var service = new RecordingService(_location, _settings, _credentials);
        var item = new RecordingItem(path, new FileInfo(path).Length, DateTime.UtcNow, RecordingKind.Terminal, null, null);
        var player = new PlayerView(service, item, (await RecordingKeys.PrivateKeyAsync(_credentials))!);
        var window = new Window { Content = player, Width = width, Height = 600 };
        window.Show();
        await WaitFor(() => player.GetVisualDescendants().OfType<TerminalView>().Any());
        return (player, window, path);
    }

    static void Press(PlayerView player, Key key, KeyModifiers modifiers = KeyModifiers.None, string? symbol = null)
    {
        player.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers, KeySymbol = symbol });
        Dispatcher.UIThread.RunJobs();
    }

    static Button PlayButton(PlayerView player) =>
        player.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("accent"));

    [AvaloniaFact]
    public async Task A_long_name_in_the_player_ends_before_the_find_box_and_buttons()
    {
        const string name = "inxe-vmh-01 · management console for the production cluster in the east data center";
        var (player, window, _) = await OpenTerminalRecording(name, width: 760);
        Dispatcher.UIThread.RunJobs();

        double Left(Control c) => c.TranslatePoint(new Point(0, 0), player)!.Value.X;
        double Right(Control c) => c.TranslatePoint(new Point(c.Bounds.Width, 0), player)!.Value.X;
        var title = player.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == name);
        var subtitle = player.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text?.StartsWith("Recorded ") == true);
        var find = player.GetVisualDescendants().OfType<TextBox>().Single(t => t.PlaceholderText == "Find in output");
        var export = player.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Export");

        // The name and subtitle share a clipped panel that ends before Find.
        var heading = (Control)subtitle.Parent!;
        Assert.True(heading.ClipToBounds);
        Assert.True(Right(title) <= Left(subtitle) + 0.5, $"The name ends at {Right(title)}, past the subtitle at {Left(subtitle)}.");
        Assert.True(Right(title) <= Left(find), $"The name ends at {Right(title)}, past Find at {Left(find)}.");
        Assert.True(Right(heading) <= Left(find), $"The heading ends at {Right(heading)}, past Find at {Left(find)}.");
        Assert.Contains("…", title.TextLayout.TextLines.SelectMany(l => l.TextRuns).Select(r => r.Text.ToString()).Aggregate("", (a, b) => a + b));
        Assert.True(Right(find) <= Left(export));
        Assert.True(Right(export) <= player.Bounds.Width);
        player.Dispose();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Loop_is_off_at_first_and_plays_the_recording_again_when_on()
    {
        var (player, window, _) = await OpenTerminalRecording("web-01");
        var loop = player.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Loop");
        try
        {
            Assert.False(player.Loops);
            Assert.DoesNotContain("selected", loop.Classes);
            var speed = player.GetVisualDescendants().OfType<ComboBox>().Single(c => AutomationProperties.GetName(c) == "Playback speed");
            speed.SelectedIndex = 4; // 8×: the second plays in an eighth

            // Off: it stops at the end and says so, and Play plays it again.
            await WaitFor(() => AutomationProperties.GetName(PlayButton(player)) == "Play again");
            Assert.Contains(player.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text?.StartsWith("End of recording") == true);

            Press(player, Key.L);
            Assert.True(player.Loops);
            Assert.Contains("selected", loop.Classes);
            PlayButton(player).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain(player.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text?.StartsWith("End of recording") == true);

            // On: several times round, it's still playing.
            for (int i = 0; i < 40; i++)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(10);
                Assert.Equal("Pause", AutomationProperties.GetName(PlayButton(player)));
            }
        }
        finally
        {
            if (player.Loops)
            {
                Press(player, Key.L); // Loop is kept for the run; leave it off for the other tests
            }

            player.Dispose();
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Shortcuts_show_with_question_mark_and_delete_and_markers_have_keys()
    {
        var (player, window, _) = await OpenTerminalRecording("web-01");
        Press(player, Key.Space); // pause

        Press(player, Key.OemQuestion, KeyModifiers.Shift, "?");
        var card = player.GetVisualDescendants().OfType<Border>().Single(b => AutomationProperties.GetName(b) == "Keyboard shortcuts");
        Assert.True(card.IsEffectivelyVisible);
        Assert.Contains(card.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Find in output");
        Assert.DoesNotContain(card.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Mute"); // no sound in this one
        Press(player, Key.Escape);
        Assert.False(card.IsEffectivelyVisible);

        // Shift+Right goes to the marker, half a second in.
        Press(player, Key.Home);
        Press(player, Key.Right, KeyModifiers.Shift);
        var timeline = player.GetVisualDescendants().OfType<Slider>().Single(s => AutomationProperties.GetName(s) == "Position");
        Assert.InRange(timeline.Value, 0.45, 0.55);

        // Find's matches show on the timeline.
        player.GetVisualDescendants().OfType<TextBox>().Single(t => t.PlaceholderText == "Find in output").Text = "deploy";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, player.GetVisualDescendants().OfType<Border>().Count(b => AutomationProperties.GetName(b)?.StartsWith("Match at ") == true));

        bool deleteAsked = false;
        player.DeleteRequested += (_, _) => deleteAsked = true;
        Press(player, Key.Delete);
        Assert.True(deleteAsked);
        player.Dispose();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Recordings_play_with_the_viewers_color_correction()
    {
        _settings.Save(new AppSettings { ColorVision = Orrery.Core.Settings.ColorVision.Deuteranopia });
        var publicKey = await RecordingKeys.EnsureAsync(_credentials);
        var path = Path.Combine(_root, "red.orrec");
        var red = new byte[16 * 8 * 3];
        for (int i = 0; i < red.Length; i += 3)
        {
            red[i] = 220;
            red[i + 1] = 40;
            red[i + 2] = 40;
        }

        using (var writer = RecordingWriter.Create(path, RecordingKind.Screen, publicKey))
        {
            writer.Append(RecordingEventType.Resize, TimeSpan.Zero, RecordingFile.Pair(16, 8));
            writer.Append(RecordingEventType.KeyFrame, TimeSpan.Zero, ScreenFrames.EncodeKeyFrame(red, 16, 8));
            writer.Complete(TimeSpan.FromSeconds(1));
        }

        var service = new RecordingService(_location, _settings, _credentials);
        var item = new RecordingItem(path, new FileInfo(path).Length, DateTime.UtcNow, RecordingKind.Screen, null, null);
        var player = new PlayerView(service, item, (await RecordingKeys.PrivateKeyAsync(_credentials))!);
        var window = new Window { Content = player, Width = 1000, Height = 600 };
        window.Show();
        await WaitFor(() => player.GetVisualDescendants().OfType<Image>().Any(i => i.Source is not null));

        byte[] FirstPixel() => player.Picture()[..3];

        // The file keeps the colors as they were; they're corrected as they're shown.
        Assert.NotNull(player.Correction);
        var corrected = FirstPixel();
        Assert.Equal(220, corrected[0]); // red-green correction keeps red and moves what is lost into blue
        Assert.True(corrected[2] > 60, $"Blue is {corrected[2]}.");

        // Turning it off in Settings shows them as recorded.
        player.SetColors(Orrery.Core.Settings.ColorVision.None, null, TerminalColorScheme.Standard);
        Assert.Equal(new byte[] { 220, 40, 40 }, FirstPixel());
        player.Dispose();
        window.Close();

        // A terminal recording plays with the color-blind palette.
        var (terminalPlayer, terminalWindow, _) = await OpenTerminalRecording("web-01");
        Assert.Equal(Orrery.Core.Settings.ColorVision.Deuteranopia, terminalPlayer.GetVisualDescendants().OfType<TerminalView>().Single().ColorVision);
        terminalPlayer.Dispose();
        terminalWindow.Close();
    }

    [AvaloniaFact]
    public async Task A_connection_with_correction_off_plays_its_own_colors()
    {
        _settings.Save(new AppSettings { ColorVision = Orrery.Core.Settings.ColorVision.Deuteranopia });
        var (player, window) = await OpenRedScreen(new RecordingInfo("desk", "RDP", DateTimeOffset.Now, ColorCorrection: false));
        Assert.Null(player.Correction);
        Assert.Equal(new byte[] { 220, 40, 40 }, player.Picture()[..3]);
        player.Dispose();
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_web_page_recorded_corrected_is_not_corrected_again()
    {
        // The page was shown, so recorded, with the viewer's own correction: it plays as recorded.
        _settings.Save(new AppSettings { ColorVision = Orrery.Core.Settings.ColorVision.Deuteranopia });
        var recordedWith = ColorCorrection.For(Orrery.Core.Settings.ColorVision.Deuteranopia, 100)!;
        var shown = recordedWith.Apply(220, 40, 40);
        var info = new RecordingInfo("router", "HTTPS", DateTimeOffset.Now, ColorCorrection: true,
            CorrectedFor: Orrery.Core.Settings.ColorVision.Deuteranopia, CorrectedStrength: 100);
        var (player, window) = await OpenRedScreen(info, (shown.R, shown.G, shown.B));
        Assert.Null(player.Correction);
        Assert.Equal(new byte[] { shown.R, shown.G, shown.B }, player.Picture()[..3]);

        // A viewer with no correction sees the page's own colors again, within rounding.
        player.SetColors(Orrery.Core.Settings.ColorVision.None, null, TerminalColorScheme.Standard);
        Assert.NotNull(player.Correction);
        var native = player.Picture()[..3];
        Assert.InRange(native[0], 218, 222);
        Assert.InRange(native[1], 38, 42);
        Assert.InRange(native[2], 38, 42);
        player.Dispose();
        window.Close();

        // An export has the page's own colors too, as every other recording's are.
        var undo = RecordingExport.NativeColors(Path.Combine(_root, "red.orrec"), (await RecordingKeys.PrivateKeyAsync(_credentials))!)!;
        byte[] pixel = [shown.R, shown.G, shown.B];
        undo(pixel);
        Assert.InRange(pixel[0], 218, 222);
        Assert.InRange(pixel[1], 38, 42);
    }

    /// <summary>A one-picture screen recording of a single color, red unless said, in a player.</summary>
    async Task<(PlayerView Player, Window Window)> OpenRedScreen(RecordingInfo info, (byte R, byte G, byte B)? color = null)
    {
        var (r, g, b) = color ?? ((byte)220, (byte)40, (byte)40);
        var publicKey = await RecordingKeys.EnsureAsync(_credentials);
        var path = Path.Combine(_root, "red.orrec");
        var pixels = new byte[16 * 8 * 3];
        for (int i = 0; i < pixels.Length; i += 3)
        {
            (pixels[i], pixels[i + 1], pixels[i + 2]) = (r, g, b);
        }

        using (var writer = RecordingWriter.Create(path, RecordingKind.Screen, publicKey))
        {
            writer.Append(RecordingEventType.Info, TimeSpan.Zero, RecordingFile.InfoPayload(info));
            writer.Append(RecordingEventType.Resize, TimeSpan.Zero, RecordingFile.Pair(16, 8));
            writer.Append(RecordingEventType.KeyFrame, TimeSpan.Zero, ScreenFrames.EncodeKeyFrame(pixels, 16, 8));
            writer.Complete(TimeSpan.FromSeconds(1));
        }

        var service = new RecordingService(_location, _settings, _credentials);
        var item = new RecordingItem(path, new FileInfo(path).Length, DateTime.UtcNow, RecordingKind.Screen, null, null);
        var player = new PlayerView(service, item, (await RecordingKeys.PrivateKeyAsync(_credentials))!);
        var window = new Window { Content = player, Width = 1000, Height = 600 };
        window.Show();
        await WaitFor(() => player.GetVisualDescendants().OfType<Image>().Any(i => i.Source is not null));
        return (player, window);
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
