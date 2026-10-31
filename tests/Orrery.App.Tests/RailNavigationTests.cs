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
