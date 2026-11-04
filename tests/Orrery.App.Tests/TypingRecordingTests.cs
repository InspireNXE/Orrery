using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Orrery.App.Connections;
using Orrery.App.Controls;
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

public sealed class TypingRecordingTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));
    readonly DataLocation _location;
    readonly AppSettingsStore _settings;
    readonly KeychainCredentialStore _credentials;

    public TypingRecordingTests()
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

    async Task<PropertiesPanel> PanelAsync(ConnectionEntry entry, ConnectionFolder? folder = null)
    {
        var tree = ConnectionTree.Empty();
        if (folder is not null)
        {
            tree.Add(folder, tree.Root);
        }

        tree.Add(entry, folder ?? tree.Root);
        var panel = new PropertiesPanel(tree, _credentials);
        new Window { Content = panel, Width = 400, Height = 900 }.Show();
        await panel.ShowAsync(entry);
        Dispatcher.UIThread.RunJobs();
        return panel;
    }

    static ToggleSwitch Switch(Control panel) => panel.GetVisualDescendants().OfType<ToggleSwitch>().Single(c => c.Name == "RecordTyping");

    static Notice Warning(Control panel) => panel.GetVisualDescendants().OfType<Notice>().Single(n => n.Text == PropertiesPanel.RecordTypingWarning);

    [AvaloniaFact]
    public async Task The_typing_switch_is_off_by_default_saved_and_inherited()
    {
        var folder = new ConnectionFolder { Name = "Audited", Settings = new InheritableSettings { RecordTyping = true } };
        var inherits = new ConnectionEntry { Name = "box", Host = "box.example", Settings = new InheritableSettings { Protocol = Protocol.Ssh } };
        Assert.False(SettingsResolver.Resolve(new ConnectionEntry { Name = "plain", Host = "plain.example" }).RecordTyping.Value);

        var panel = await PanelAsync(inherits, folder);
        Assert.True(Switch(panel).IsChecked);
        Assert.True(SettingsResolver.Resolve(inherits).RecordTyping.Value);

        Switch(panel).IsChecked = false;
        Assert.True(panel.TrySave());
        Assert.False(inherits.Settings.RecordTyping);
        Assert.False(SettingsResolver.Resolve(inherits).RecordTyping.Value);
    }

    [AvaloniaFact]
    public async Task Turning_it_on_shows_a_warning_about_passwords_and_pastes()
    {
        var panel = await PanelAsync(new ConnectionEntry { Name = "box", Host = "box.example", Settings = new InheritableSettings { Protocol = Protocol.Ssh } });
        Assert.False(Warning(panel).IsVisible);

        Switch(panel).IsChecked = true;
        Assert.True(Warning(panel).IsVisible);
        Assert.Contains("password typed at any other prompt", PropertiesPanel.RecordTypingWarning);
        Assert.Contains("pasted", PropertiesPanel.RecordTypingWarning);
        Assert.Contains("encrypted", PropertiesPanel.RecordTypingWarning);
        Assert.Contains("Recording, typing too", PropertiesPanel.RecordTypingWarning);
    }

    [AvaloniaFact]
    public async Task A_remote_desktop_has_no_typing_switch()
    {
        var panel = await PanelAsync(new ConnectionEntry { Name = "desk", Host = "desk.example", Settings = new InheritableSettings { Protocol = Protocol.Rdp, RecordTyping = true } });
        Assert.False(Switch(panel).IsEffectivelyVisible);
        Assert.False(Warning(panel).IsVisible);
    }

    [AvaloniaFact]
    public async Task Keys_and_commands_are_recorded_but_not_secrets_or_a_password_prompt()
    {
        var service = new RecordingService(_location, _settings, _credentials);
        var session = new Terminal();
        var view = new TerminalView(session);
        var tab = new SessionTab(session, view, new SessionOrigin("web-01", Protocol.Ssh, null, () => Task.CompletedTask));
        var window = new Window { Content = new DockPanel { Children = { tab.Header, tab.Host } }, Width = 800, Height = 600 };
        window.Show();

        var (recorder, problem) = await service.StartAsync(tab, window, recordTyping: true);
        Assert.Null(problem);
        tab.Recorder = recorder;
        Assert.EndsWith(", recording, typing too", tab.Header.Label);
        Assert.Equal("Recording, typing too", tab.RecordingText);

        session.Output("$ ");
        Dispatcher.UIThread.RunJobs();
        view.Focus();
        window.KeyTextInput("uptime");
        view.SendCommand("df -h", pressEnter: true);
        view.SendSecret("saved-secret");

        session.Output("\r\n[sudo] password for admin: ");
        Dispatcher.UIThread.RunJobs();
        window.KeyTextInput("hunter2");

        recorder!.Stop();
        await recorder.Completion;

        var privateKey = (await RecordingKeys.PrivateKeyAsync(_credentials))!;
        using var reader = RecordingReader.Open(recorder.Path, privateKey);
        var events = reader.Events().ToList();
        var typed = string.Concat(events.Where(e => e.Type == RecordingEventType.Input).Select(e => Encoding.UTF8.GetString(e.Payload)));
        Assert.Contains("uptime", typed);
        Assert.Contains("df -h", typed);
        Assert.DoesNotContain("saved-secret", typed);
        Assert.DoesNotContain("hunter", typed);
        Assert.Single(events, e => e.Type == RecordingEventType.Marker && Encoding.UTF8.GetString(e.Payload) == TerminalRecorder.TypingHeldMarker);
        Assert.Equal(RecordingFile.VersionWithTyping, reader.Header.Version);
    }

    [AvaloniaFact]
    public async Task Without_the_setting_a_recording_keeps_nothing_typed()
    {
        var service = new RecordingService(_location, _settings, _credentials);
        var session = new Terminal();
        var view = new TerminalView(session);
        var tab = new SessionTab(session, view, new SessionOrigin("web-01", Protocol.Ssh, null, () => Task.CompletedTask));
        var window = new Window { Content = new DockPanel { Children = { tab.Header, tab.Host } }, Width = 800, Height = 600 };
        window.Show();

        var (recorder, _) = await service.StartAsync(tab, window);
        tab.Recorder = recorder;
        Assert.EndsWith(", recording", tab.Header.Label);
        view.Focus();
        window.KeyTextInput("uptime");
        recorder!.Stop();
        await recorder.Completion;

        using var reader = RecordingReader.Open(recorder.Path, (await RecordingKeys.PrivateKeyAsync(_credentials))!);
        Assert.DoesNotContain(reader.Events(), e => e.Type == RecordingEventType.Input);
        Assert.Equal(RecordingFile.Version, reader.Header.Version);
    }

    [AvaloniaFact]
    public async Task The_player_shows_what_was_typed_as_it_plays()
    {
        var publicKey = await RecordingKeys.EnsureAsync(_credentials);
        var path = Path.Combine(_root, "typed.orrec");
        using (var writer = RecordingWriter.Create(path, RecordingKind.Terminal, publicKey, typing: true))
        {
            writer.Append(RecordingEventType.Info, TimeSpan.Zero, RecordingFile.InfoPayload(new RecordingInfo("web-01", "SSH", DateTimeOffset.Now)));
            writer.Append(RecordingEventType.Resize, TimeSpan.Zero, RecordingFile.Pair(40, 10));
            writer.Append(RecordingEventType.Input, TimeSpan.FromSeconds(1), "lsx\x7f -l\r"u8);
            writer.Append(RecordingEventType.Input, TimeSpan.FromSeconds(2), "who\x1b[D"u8);
            writer.Complete(TimeSpan.FromSeconds(3));
        }

        var service = new RecordingService(_location, _settings, _credentials);
        var item = new RecordingItem(path, new FileInfo(path).Length, DateTime.UtcNow, RecordingKind.Terminal, null, null);
        var player = new PlayerView(service, item, (await RecordingKeys.PrivateKeyAsync(_credentials))!);
        var window = new Window { Content = player, Width = 1000, Height = 720 };
        window.Show();
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!player.GetVisualDescendants().OfType<TerminalView>().Any() && DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }

        Assert.Equal("Typed: nothing yet", player.TypedText);
        var timeline = player.GetVisualDescendants().OfType<Slider>().Single(s => Avalonia.Automation.AutomationProperties.GetName(s) == "Position");
        timeline.Value = timeline.Maximum;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Typing: who", player.TypedText);

        player.Dispose();
        window.Close();
    }
}
