using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Orrery.App.Input;
using Orrery.Core.Settings;
using Orrery.Core.Storage;
using Xunit;

namespace Orrery.App.Tests;

/// <summary>AltGr arrives as Ctrl+Alt on Windows, so Ctrl+Alt+B leaves a key that types a character to the session.</summary>
public class AltGrShortcutTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));

    [AvaloniaFact]
    public void Ctrl_Alt_B_that_types_a_brace_reaches_the_session_instead_of_toggling_the_settings_pane()
    {
        if (OperatingSystem.IsMacOS())
        {
            return; // The pane's shortcut is Cmd+Option+B there, and AltGr isn't Ctrl+Alt.
        }

        var location = new DataLocation(StorageMode.Portable, Path.Combine(_dir, "data"), Path.Combine(_dir, "cache"), Path.Combine(_dir, "logs"));
        Directory.CreateDirectory(location.DataDir);
        AppSettingsStore.In(location).Save(new AppSettings { InspectorMode = PaneMode.Hidden });
        VaultAutoLockTests.AddMainWindowResources();
        var window = new MainWindow(location) { Width = 1400, Height = 900 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var inspector = VaultAutoLockTests.Private<Border>(window, "_propertiesHost");

            // A Czech keyboard: AltGr+B is {.
            window.TypesWithAltGr = key => key == Key.B;
            window.KeyPress(Key.B, RawInputModifiers.Control | RawInputModifiers.Alt, PhysicalKey.B, "b");
            Dispatcher.UIThread.RunJobs();
            Assert.False(inspector.IsVisible);

            // As a web page offers the key: left unhandled, so the page gets it.
            var offered = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.B, KeyModifiers = KeyModifiers.Control | KeyModifiers.Alt };
            window.RaiseEvent(offered);
            Assert.False(offered.Handled);
            Assert.False(inspector.IsVisible);

            // A US keyboard: Ctrl+Alt+B types nothing, so it's the shortcut.
            window.TypesWithAltGr = _ => false;
            window.KeyPress(Key.B, RawInputModifiers.Control | RawInputModifiers.Alt, PhysicalKey.B, "b");
            Dispatcher.UIThread.RunJobs();
            Assert.True(inspector.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [Theory]
    [InlineData(Key.Enter)]
    [InlineData(Key.Home)]
    [InlineData(Key.End)]
    [InlineData(Key.PageUp)]
    public void Keys_that_never_type_are_never_taken_for_AltGr(Key key) => Assert.False(AltGr.TypesCharacter(key));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        }
    }
}
