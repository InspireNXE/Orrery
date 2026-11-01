using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
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

    [AvaloniaFact]
    public void A_long_folder_name_keeps_every_rows_badge_inside_the_pane()
    {
        var location = new DataLocation(StorageMode.Portable, Path.Combine(_dir, "data"), Path.Combine(_dir, "cache"), Path.Combine(_dir, "logs"));
        Directory.CreateDirectory(location.DataDir);
        var longName = "Imported from Orrery (2026-11-01) with a much longer name than the pane has room for";
        File.WriteAllText(Path.Combine(location.DataDir, "connections.json"), """
            {"schemaVersion":1,"root":{"type":"folder","name":"Connections","id":"00000000-0000-0000-0000-000000000001","children":[
              {"type":"folder","name":"LONG","id":"00000000-0000-0000-0000-000000000002","children":[
                {"type":"connection","name":"desk","id":"00000000-0000-0000-0000-000000000003","host":"desk.example","settings":{"protocol":"rdp"}}]}]}}
            """.Replace("LONG", longName));
        VaultAutoLockTests.AddMainWindowResources();
        var window = new MainWindow(location) { Width = 1400, Height = 900 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var tree = window.FindControl<TreeView>("Tree")!;
            var badge = tree.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "RDP" && t.IsEffectivelyVisible);
            var right = badge.TranslatePoint(new Avalonia.Point(badge.Bounds.Width, 0), tree)!.Value.X;
            Assert.InRange(right, 0, tree.Bounds.Width);

            var folderName = tree.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == longName);
            Assert.True(folderName.TranslatePoint(new Avalonia.Point(folderName.Bounds.Width, 0), tree)!.Value.X <= tree.Bounds.Width);
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
