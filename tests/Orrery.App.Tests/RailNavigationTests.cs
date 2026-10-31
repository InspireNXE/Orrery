using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Orrery.Core.Storage;
using Xunit;

namespace Orrery.App.Tests;

public class RailNavigationTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));

    [AvaloniaTheory]
    [InlineData("ConnectionsRailButton")] // The panel that was already showing behind Settings.
    [InlineData("CredentialsRailButton")]
    [InlineData("RecentRailButton")]
    public void Choosing_another_rail_item_leaves_settings_and_shows_its_panel(string button)
    {
        var location = new DataLocation(StorageMode.Portable, Path.Combine(_dir, "data"), Path.Combine(_dir, "cache"), Path.Combine(_dir, "logs"));
        Directory.CreateDirectory(location.DataDir);
        VaultAutoLockTests.AddMainWindowResources();
        var window = new MainWindow(location);
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var settings = VaultAutoLockTests.Private<ContentControl>(window, "_settingsHost");
            var sidePanel = VaultAutoLockTests.Private<Border>(window, "_sidePanel");

            Click(window, "SettingsRailButton");
            Assert.True(settings.IsVisible);

            Click(window, button);
            Assert.False(settings.IsVisible);
            Assert.True(sidePanel.IsVisible);
            Assert.DoesNotContain("active", window.FindControl<Button>("SettingsRailButton")!.Classes);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Settings_covers_the_side_panel_and_the_inspector_and_is_the_only_rail_item_lit()
    {
        var location = new DataLocation(StorageMode.Portable, Path.Combine(_dir, "data"), Path.Combine(_dir, "cache"), Path.Combine(_dir, "logs"));
        Directory.CreateDirectory(location.DataDir);
        VaultAutoLockTests.AddMainWindowResources();
        var window = new MainWindow(location) { Width = 1400, Height = 900 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var settings = VaultAutoLockTests.Private<ContentControl>(window, "_settingsHost");
            var sidePanel = VaultAutoLockTests.Private<Border>(window, "_sidePanel");
            var inspector = VaultAutoLockTests.Private<Border>(window, "_propertiesHost");
            Assert.True(sidePanel.IsVisible);

            Click(window, "SettingsRailButton");
            window.UpdateLayout();

            // Settings starts where the side panel does and runs past the inspector.
            Assert.Equal(sidePanel.Bounds.Left, settings.Bounds.Left);
            Assert.Equal(inspector.Bounds.Right, settings.Bounds.Right);
            foreach (var name in new[] { "ConnectionsRailButton", "CredentialsRailButton", "RecentRailButton" })
            {
                Assert.DoesNotContain("active", window.FindControl<Button>(name)!.Classes);
            }

            Click(window, "SettingsRailButton");
            Assert.Contains("active", window.FindControl<Button>("ConnectionsRailButton")!.Classes);
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
