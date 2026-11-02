using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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

public class FullScreenDisplaysTests
{
    // A laptop, a 1440p display beside it, and a portrait display beyond that.
    static readonly LocalDisplay[] Three =
    [
        new(new ScreenArea(0, 360, 1920, 1080, true), "DELL U2419H"),
        new(new ScreenArea(1920, 0, 2560, 1440, false), "LG 27GP850"),
        new(new ScreenArea(4480, 0, 1080, 1920, false), "DELL P2419H"),
    ];

    static List<ToggleButton> Toggles(Control root) => root.GetVisualDescendants().OfType<ToggleButton>()
        .Where(t => AutomationProperties.GetName(t)?.StartsWith("Display ", StringComparison.Ordinal) == true).ToList();

    static (DisplayPicker Picker, Window Window) Picker(bool several = true)
    {
        var picker = new DisplayPicker(() => Three, several);
        var window = new Window { Content = picker, Width = 400, Height = 400 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (picker, window);
    }

    [AvaloniaFact]
    public void The_outer_two_displays_are_chosen_and_said_to_sit_side_by_side_on_the_remote_computer()
    {
        var (picker, window) = Picker();
        var toggles = Toggles(window);
        Assert.Equal(3, toggles.Count);
        Assert.Contains("main", AutomationProperties.GetName(toggles[0]));

        toggles[0].IsChecked = true;
        toggles[2].IsChecked = true;
        Assert.Equal("Displays 1 and 3", picker.Describe());
        Assert.Equal([ChosenDisplay.Of(Three[0].Area, "DELL U2419H"), ChosenDisplay.Of(Three[2].Area, "DELL P2419H")], picker.Chosen);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text?.Contains("sit side by side on the remote computer") == true);
        window.Close();
    }

    [AvaloniaFact]
    public void A_chosen_display_that_isnt_connected_is_kept_and_said()
    {
        var (picker, window) = Picker();
        var gone = new ChosenDisplay(9000, 0, 1280, 1024, "Projector");
        picker.Chosen = [ChosenDisplay.Of(Three[1].Area), gone];

        Assert.Equal([false, true, false], Toggles(window).Select(t => t.IsChecked == true));
        Assert.Contains(gone, picker.Chosen);
        Assert.Equal("Display 2, and 1 not connected", picker.Describe());
        window.Close();
    }

    [AvaloniaFact]
    public void Where_a_window_fills_one_display_choosing_one_clears_the_other()
    {
        var (picker, window) = Picker(several: false);
        var toggles = Toggles(window);
        toggles[0].IsChecked = true;
        toggles[1].IsChecked = true;
        Assert.Equal("Display 2", picker.Describe());
        window.Close();
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

    [AvaloniaFact]
    public async Task Chosen_displays_are_saved_on_the_connection_and_every_display_replaces_them()
    {
        var tree = ConnectionTree.Empty();
        var entry = new ConnectionEntry { Name = "east-gpu-01", Host = "10.20.16.3" };
        tree.Add(entry, tree.Root);
        var panel = new PropertiesPanel(tree, new NoCredentials(), displays: () => Three);
        var window = new Window { Content = panel, Width = 420, Height = 1400 };
        window.Show();
        await panel.ShowAsync(entry);
        Dispatcher.UIThread.RunJobs();
        bool Shown(string text) => window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text == text);
        Assert.True(Shown("This display"));

        // The row opens to show its editor, as a click on it does.
        var row = window.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("setting") && AutomationProperties.GetName(b)?.StartsWith("Full screen on:", StringComparison.Ordinal) == true);
        row.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        var on = window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "FullScreenOn");
        on.SelectedItem = on.Items.OfType<ComboBoxItem>().Single(i => Equals(i.Tag, FullScreenOn.ChosenDisplays));
        Dispatcher.UIThread.RunJobs();
        Assert.False(panel.TrySave()); // Nothing chosen yet.

        var toggles = Toggles(window);
        toggles[0].IsChecked = true;
        toggles[2].IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(Shown("Displays 1 and 3"));
        Assert.True(panel.TrySave());
        Assert.Equal([ChosenDisplay.Of(Three[0].Area, "DELL U2419H"), ChosenDisplay.Of(Three[2].Area, "DELL P2419H")], entry.Settings.ChosenDisplays);
        Assert.Null(entry.Settings.UseAllMonitors);

        on.SelectedItem = on.Items.OfType<ComboBoxItem>().Single(i => Equals(i.Tag, FullScreenOn.EveryDisplay));
        Assert.True(panel.TrySave());
        Assert.True(entry.Settings.UseAllMonitors);
        Assert.Null(entry.Settings.ChosenDisplays);
        Assert.Equal(FullScreenChoice.EveryDisplay, SettingsResolver.Resolve(entry).FullScreen.Value);
        window.Close();
    }

    sealed class Desktop : IFrameBufferSession
    {
        public ConnectionTarget Target { get; } = new("desk.example", 3389, "user", null);
        public SessionState State => SessionState.Connected;
        public event EventHandler<SessionStateChangedEventArgs>? StateChanged { add { } remove { } }
        public event EventHandler<FrameUpdatedEventArgs>? FrameUpdated { add { } remove { } }
        public int Width => 3000;
        public int Height => 1920;
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
    public void A_point_on_the_third_display_lands_on_the_remote_monitor_beside_the_first()
    {
        var view = new RemoteFrameView(new Desktop());

        // Displays 1 and 3 as the window covers them (display 2 cut out), and the remote desktop with them moved together.
        view.ShowPieces(
        [
            new RemoteFrameView.Piece(new PixelRect(0, 360, 1920, 1080), new PixelRect(0, 360, 1920, 1080)),
            new RemoteFrameView.Piece(new PixelRect(4480, 0, 1080, 1920), new PixelRect(1920, 0, 1080, 1920)),
        ], new PixelSize(3000, 1920));

        Assert.Equal((1920 + 10, 20), view.PieceRemotePoint(new PixelPoint(4490, 20)));
        Assert.Equal((100, 400), view.PieceRemotePoint(new PixelPoint(100, 400)));
        Assert.Null(view.PieceRemotePoint(new PixelPoint(3000, 500))); // Display 2.
    }
}
