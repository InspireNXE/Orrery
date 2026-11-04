using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Orrery.App.Connections;
using Orrery.Core.Settings;
using Orrery.Core.Storage;
using Xunit;

namespace Orrery.App.Tests;

public class PaneModeTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));

    DataLocation Location => new(StorageMode.Portable, Path.Combine(_dir, "data"), Path.Combine(_dir, "cache"), Path.Combine(_dir, "logs"));

    MainWindow Open(AppSettings settings)
    {
        Directory.CreateDirectory(Location.DataDir);
        AppSettingsStore.In(Location).Save(settings);
        VaultAutoLockTests.AddMainWindowResources();
        var window = new MainWindow(Location) { Width = 1400, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return window;
    }

    [Fact]
    public void Panes_closed_before_modes_existed_keep_what_was_on_screen()
    {
        Assert.Equal(PaneMode.Collapsed, new AppSettings { SidePanelOpen = false }.SidePanel);
        Assert.Equal(PaneMode.Hidden, new AppSettings { InspectorOpen = false }.Inspector);
        Assert.Equal(PaneMode.Expanded, new AppSettings().SidePanel);
        // Auto is no longer offered; it reads as opening over the session.
        Assert.Equal(PaneMode.Collapsed, new AppSettings { SidePanelOpen = false, SidePanelMode = PaneMode.Auto }.SidePanel);
        Assert.Equal(PaneMode.Collapsed, new AppSettings { InspectorMode = PaneMode.Auto }.Inspector);
    }

    [AvaloniaFact]
    public void The_collapsed_panes_leave_the_same_room_beside_their_buttons()
    {
        var window = Open(new AppSettings { SidePanelMode = PaneMode.Collapsed, InspectorMode = PaneMode.Collapsed });
        try
        {
            var rail = window.FindControl<Control>("Rail")!;
            var strip = window.FindControl<Control>("InspectorStrip")!;
            var button = window.FindControl<Control>("InspectorStripButton")!;
            Assert.True(rail.IsVisible && strip.IsVisible);
            Assert.Equal(rail.Bounds.Width, strip.Bounds.Width);
            var railButton = window.FindControl<Control>("RecentRailButton")!;
            // Mirror images: the rail's border is on its right, the strip's on its left, each with the same gap to its button.
            Assert.Equal(rail.Bounds.Width - railButton.TranslatePoint(default, rail)!.Value.X - railButton.Bounds.Width, button.TranslatePoint(default, strip)!.Value.X, 0);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_collapsed_side_panel_opens_over_the_sessions_and_a_click_elsewhere_closes_it()
    {
        var window = Open(new AppSettings { SidePanelMode = PaneMode.Collapsed });
        try
        {
            var side = VaultAutoLockTests.Private<Border>(window, "_sidePanel");
            var host = window.FindControl<Panel>("SessionHost")!;
            Assert.False(side.IsVisible);
            Assert.True(window.FindControl<Control>("Rail")!.IsVisible);
            double sessionsWidth = host.Bounds.Width;

            Click(window, "RecentRailButton");
            window.UpdateLayout();
            Assert.True(side.IsVisible);
            Assert.Equal(2, Grid.GetColumn(side));
            Assert.Equal(sessionsWidth, host.Bounds.Width);

            window.MouseDown(new Avalonia.Point(900, 500), MouseButton.Left);
            window.MouseUp(new Avalonia.Point(900, 500), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.False(side.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Unpinning_leaves_the_pane_open_over_the_sessions_and_pinning_docks_it_again()
    {
        var window = Open(new AppSettings());
        try
        {
            var side = VaultAutoLockTests.Private<Border>(window, "_sidePanel");
            var pin = VaultAutoLockTests.Private<List<Button>>(window, "_sidePins").First(b => b.IsEffectivelyVisible);
            Assert.Equal(1, Grid.GetColumn(side));

            pin.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.True(side.IsVisible);
            Assert.Equal(2, Grid.GetColumn(side));
            Assert.Equal(PaneMode.Collapsed, AppSettingsStore.In(Location).Load().SidePanelMode);

            pin.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, Grid.GetColumn(side));
            Assert.Equal(PaneMode.Expanded, AppSettingsStore.In(Location).Load().SidePanelMode);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Panes_open_at_the_width_they_were_dragged_to()
    {
        var window = Open(new AppSettings { SidePanelWidth = 400, InspectorWidth = 5000 });
        try
        {
            Assert.Equal(400, VaultAutoLockTests.Private<Border>(window, "_sidePanel").Width);
            Assert.Equal(640, VaultAutoLockTests.Private<Border>(window, "_propertiesHost").Width);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_hidden_settings_pane_leaves_only_the_edge_and_its_shortcut_opens_it_over_the_sessions()
    {
        var window = Open(new AppSettings { InspectorMode = PaneMode.Hidden });
        try
        {
            var inspector = VaultAutoLockTests.Private<Border>(window, "_propertiesHost");
            Assert.False(inspector.IsVisible);
            Assert.False(window.FindControl<Border>("InspectorStrip")!.IsVisible);
            Assert.True(window.FindControl<Border>("RightEdge")!.IsVisible);

            window.KeyPress(Key.B, RawInputModifiers.Control | RawInputModifiers.Alt, PhysicalKey.B, "b");
            Dispatcher.UIThread.RunJobs();
            Assert.True(inspector.IsVisible);
            Assert.Equal(2, Grid.GetColumn(inspector));

            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void The_toggle_hides_a_pinned_pane_and_shows_it_again_without_changing_its_mode()
    {
        var window = Open(new AppSettings());
        try
        {
            var side = VaultAutoLockTests.Private<Border>(window, "_sidePanel");
            Click(window, "SidePanelToggle");
            Assert.False(side.IsVisible);
            Assert.Null(AppSettingsStore.In(Location).Load().SidePanelMode);

            Click(window, "SidePanelToggle");
            Assert.True(side.IsVisible);
            Assert.Equal(1, Grid.GetColumn(side));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Panes_put_away_stay_away_at_the_next_start_and_come_back_once_shown()
    {
        var window = Open(new AppSettings());
        Click(window, "SidePanelToggle");
        Click(window, "InspectorToggle");
        window.Close();
        Assert.True(AppSettingsStore.In(Location).Load().SidePanelPutAway);
        Assert.True(AppSettingsStore.In(Location).Load().InspectorPutAway);

        window = Open(AppSettingsStore.In(Location).Load());
        try
        {
            var side = VaultAutoLockTests.Private<Border>(window, "_sidePanel");
            Assert.False(side.IsVisible);
            Assert.False(VaultAutoLockTests.Private<Border>(window, "_propertiesHost").IsVisible);

            // Still pinned: shown again, it's beside the sessions, and the next start shows it too.
            Click(window, "SidePanelToggle");
            Assert.True(side.IsVisible);
            Assert.Equal(1, Grid.GetColumn(side));
            Assert.Null(AppSettingsStore.In(Location).Load().SidePanelPutAway);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void The_side_panel_starts_on_Recent_when_it_was_left_there()
    {
        var window = Open(new AppSettings());
        Click(window, "RecentRailButton");
        window.Close();
        Assert.Equal("recent", AppSettingsStore.In(Location).Load().SidePanelPage);

        window = Open(AppSettingsStore.In(Location).Load());
        try
        {
            Assert.True(window.FindControl<Control>("RecentSide")!.IsVisible);
            Assert.False(window.FindControl<Control>("ConnectionsSide")!.IsVisible);

            Click(window, "ConnectionsRailButton");
            Assert.Null(AppSettingsStore.In(Location).Load().SidePanelPage);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void The_side_panel_starts_on_Commands_with_its_editor_beside_it_when_it_was_left_there()
    {
        var window = Open(new AppSettings());
        Click(window, "CommandsRailButton");
        Assert.IsType<CommandEditorPane>(VaultAutoLockTests.Private<ContentControl>(window, "_inspectorContent").Content);
        window.Close();
        Assert.Equal("commands", AppSettingsStore.In(Location).Load().SidePanelPage);

        window = Open(AppSettingsStore.In(Location).Load());
        try
        {
            Assert.True(window.FindControl<Control>("CommandsSide")!.IsVisible);
            Assert.False(window.FindControl<Control>("ConnectionsSide")!.IsVisible);
            Assert.IsType<CommandEditorPane>(VaultAutoLockTests.Private<ContentControl>(window, "_inspectorContent").Content);

            Click(window, "ConnectionsRailButton");
            Assert.IsType<PropertiesPanel>(VaultAutoLockTests.Private<ContentControl>(window, "_inspectorContent").Content);
        }
        finally
        {
            window.Close();
        }
    }

    static void Click(Window window, string name)
    {
        window.FindControl<Button>(name)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        }
    }
}
