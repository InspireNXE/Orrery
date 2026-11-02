using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Orrery.App.Connections;
using Orrery.App.Sessions;
using Orrery.Core.Connections;
using Orrery.Core.Credentials;
using Orrery.Core.Sessions;
using Xunit;

namespace Orrery.App.Tests;

public class SessionSettingsTests
{
    sealed class Desktop : IFrameBufferSession
    {
        public ConnectionTarget Target { get; } = new("desk.example", 3389, "user", null);
        public SessionState State { get; set; } = SessionState.Connected;
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

    sealed class NoCredentials : ICredentialStore
    {
        public bool IsLocked => false;
        public bool Exists => true;
        public Task UnlockAsync(string masterPassword) => Task.CompletedTask;
        public void Lock() { }
        public Task<IReadOnlyList<CredentialSummary>> ListAsync() => Task.FromResult<IReadOnlyList<CredentialSummary>>([]);
        public Task<Credential?> GetAsync(Guid id) => Task.FromResult<Credential?>(null);
        public Task SaveAsync(Credential credential) => Task.CompletedTask;
        public Task SaveAllAsync(IReadOnlyList<Credential> credentials) => Task.CompletedTask;
        public Task DeleteAsync(Guid id) => Task.CompletedTask;
    }

    static async Task<(PropertiesPanel Panel, Window Window, RemoteFrameView View, ConnectionEntry Entry, SessionTab Tab, ConnectionTree Tree)> Open(Protocol protocol = Protocol.Rdp)
    {
        var tree = ConnectionTree.Empty();
        var folder = new ConnectionFolder { Name = "Lab servers", Settings = { Scaling = DesktopScaling.ShrinkToFit } };
        var entry = new ConnectionEntry { Name = "inxe-gpc-01", Host = "10.52.16.3", Settings = { Protocol = protocol } };
        tree.Add(folder, tree.Root);
        tree.Add(entry, folder);

        var desktop = new Desktop();
        var view = new RemoteFrameView(desktop) { Scaling = DesktopScaling.ShrinkToFit };
        var tab = new SessionTab(desktop, view, new SessionOrigin(entry.Name, protocol, entry.Id, () => Task.CompletedTask));
        var panel = new PropertiesPanel(tree, new NoCredentials());
        var window = new Window { Content = panel, Width = 420, Height = 1000 };
        window.Show();
        await panel.ShowAsync(entry);
        panel.ShowSession(tab);
        Dispatcher.UIThread.RunJobs();
        return (panel, window, view, entry, tab, tree);
    }

    static Button ButtonNamed(Window window, string content) =>
        window.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && (Equals(b.Content, content)
            || b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == content)));

    static void Click(Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    static bool Shown(Window window, string text) =>
        window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text == text);

    [AvaloniaFact]
    public async Task A_change_reaches_the_open_desktop_and_keeping_it_saves_it_on_the_connection()
    {
        var (_, window, view, entry, _, _) = await Open();
        Click(ButtonNamed(window, "This session"));

        var scaling = window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.IsEffectivelyVisible && c.Name == "Scaling");
        scaling.SelectedItem = scaling.Items.OfType<ComboBoxItem>().Single(i => Equals(i.Tag, DesktopScaling.ActualSize));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(DesktopScaling.ActualSize, view.Scaling);
        Assert.Null(entry.Settings.Scaling); // Only this session so far.
        Assert.True(Shown(window, "CHANGED"));
        Assert.True(Shown(window, "Saved: Shrink to fit"));
        Assert.True(Shown(window, "1 change for this session only"));

        Click(window.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && Equals(b.Content, "Keep for next time")));
        Assert.Equal(DesktopScaling.ActualSize, entry.Settings.Scaling);
        Assert.False(Shown(window, "CHANGED"));
        Assert.True(Shown(window, "This session")); // Still on the session's tab.
        Assert.False(Shown(window, "1 change for this session only"));
    }

    [AvaloniaFact]
    public async Task Undo_puts_the_saved_values_back_and_color_correction_turns_off_at_once()
    {
        var (_, window, view, entry, _, _) = await Open();
        Click(ButtonNamed(window, "This session"));

        var correction = window.GetVisualDescendants().OfType<ToggleSwitch>().Single(t => t.IsEffectivelyVisible && t.Name == "ColorCorrection");
        Assert.True(correction.IsChecked);
        correction.IsChecked = false;
        Dispatcher.UIThread.RunJobs();
        Assert.False(view.AllowsColorCorrection);
        Assert.True(Shown(window, "1 change for this session only"));

        Click(ButtonNamed(window, "Undo"));
        Assert.True(view.AllowsColorCorrection);
        Assert.True(correction.IsChecked);
        Assert.Null(entry.Settings.ColorCorrection);
    }

    [AvaloniaFact]
    public async Task A_reconnect_is_asked_for_only_once_a_setting_fixed_at_the_start_is_saved_differently()
    {
        var (panel, window, _, entry, tab, tree) = await Open();
        var reconnects = new List<SessionTab>();
        panel.ReconnectRequested += (_, t) => reconnects.Add(t);
        Click(ButtonNamed(window, "This session"));
        Assert.False(Shown(window, "Need a reconnect")); // Nothing changed since it started.

        entry.Settings.RemoteAudio = RemoteAudio.DoNotPlay;
        tree.NotifyChanged();
        Dispatcher.UIThread.RunJobs();
        Assert.True(Shown(window, "Need a reconnect"));
        Assert.True(Shown(window, "Remote audio"));
        Assert.True(Shown(window, "This session: Play here"));
        Assert.False(Shown(window, "Display quality")); // Unchanged, so not listed.
        Click(ButtonNamed(window, "Reconnect"));
        Assert.Equal([tab], reconnects);

        // Put back as it was: nothing to reconnect for.
        entry.Settings.RemoteAudio = null;
        tree.NotifyChanged();
        Dispatcher.UIThread.RunJobs();
        Assert.False(Shown(window, "Need a reconnect"));

        // Another connection shown: its saved settings only.
        var other = new ConnectionEntry { Name = "other", Host = "other.example" };
        panel.ShowSession(tab);
        await panel.ShowAsync(other);
        Dispatcher.UIThread.RunJobs();
        Assert.False(Shown(window, "This session"));
        Assert.False(Shown(window, "Need a reconnect"));
    }
}
