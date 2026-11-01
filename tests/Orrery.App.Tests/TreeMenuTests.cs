using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Orrery.Core.Storage;
using Xunit;

namespace Orrery.App.Tests;

public class TreeMenuTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));

    [AvaloniaFact]
    public void With_nothing_selected_export_is_grayed_out_and_the_rdp_file_items_are_there()
    {
        var location = new DataLocation(StorageMode.Portable, Path.Combine(_dir, "data"), Path.Combine(_dir, "cache"), Path.Combine(_dir, "logs"));
        Directory.CreateDirectory(location.DataDir);
        VaultAutoLockTests.AddMainWindowResources();
        var window = new MainWindow(location) { Width = 1400, Height = 900 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var tree = window.FindControl<TreeView>("Tree")!;
            var menu = tree.ContextMenu!;
            tree.RaiseEvent(new ContextRequestedEventArgs()); // As a right-click does, which raises the menu's Opening.
            Dispatcher.UIThread.RunJobs();
            Assert.True(menu.IsOpen);

            var items = menu.Items.OfType<MenuItem>().ToDictionary(i => (string)i.Header!);
            Assert.False(items["Export…"].IsEnabled);
            Assert.False(items["Duplicate"].IsEnabled);
            Assert.False(items["Delete"].IsEnabled);
            Assert.True(items["Open Remote Desktop file…"].IsEnabled);
            Assert.True(items["Import…"].IsEnabled);
            menu.Close();
        }
        finally
        {
            window.Close();
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        }
    }
}
