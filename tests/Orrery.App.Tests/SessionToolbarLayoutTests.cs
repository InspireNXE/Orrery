using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Orrery.Core.Storage;
using Xunit;

namespace Orrery.App.Tests;

public class SessionToolbarLayoutTests
{
    [AvaloniaFact]
    public void In_a_narrow_window_the_address_and_detail_stop_short_of_the_buttons()
    {
        var dir = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));
        var location = new DataLocation(StorageMode.Portable, Path.Combine(dir, "data"), Path.Combine(dir, "cache"), Path.Combine(dir, "logs"));
        Directory.CreateDirectory(location.DataDir);
        VaultAutoLockTests.AddMainWindowResources();
        var window = new MainWindow(location) { Width = 520, Height = 400 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var toolbar = window.FindControl<Border>("SessionToolbar")!;
            var address = window.FindControl<TextBlock>("SessionAddress")!;
            var detail = window.FindControl<TextBlock>("SessionDetail")!;
            var actions = window.FindControl<StackPanel>("SessionActions")!;

            // What a local shell's bar holds: a long user@host, its detail, and the usual buttons.
            toolbar.IsVisible = true;
            address.Text = "MCS@DESKTOP-SNB1S7O-WITH-A-LONG-NAME";
            detail.Text = "LOCAL · Running · PowerShell 7.5 · started a while ago";
            for (int i = 0; i < 4; i++)
            {
                actions.Children.Add(new Button { Width = 32, Content = "·" });
            }
            actions.Children.Add(new Button { Width = 60, Content = "End" });
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            double buttonsStart = actions.TranslatePoint(new Point(0, 0), toolbar)!.Value.X;
            double addressEnd = address.TranslatePoint(new Point(address.Bounds.Width, 0), toolbar)!.Value.X;
            double detailEnd = detail.TranslatePoint(new Point(detail.Bounds.Width, 0), toolbar)!.Value.X;
            Assert.True(addressEnd <= buttonsStart, $"address ends at {addressEnd}, buttons start at {buttonsStart}");
            Assert.True(detailEnd <= buttonsStart, $"detail ends at {detailEnd}, buttons start at {buttonsStart}");
        }
        finally
        {
            window.Close();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
