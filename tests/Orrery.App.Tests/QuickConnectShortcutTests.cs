using System.Reflection;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Orrery.App.Connections;
using Orrery.App.Sessions;
using Orrery.Core.Connections;
using Orrery.Core.Sessions;
using Orrery.Core.Storage;
using Xunit;

namespace Orrery.App.Tests;

public class QuickConnectShortcutTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));

    sealed class ShellSession : ITerminalSession
    {
        public ConnectionTarget Target { get; } = new("box.example", 22, "admin", null);
        public SessionState State => SessionState.Connected;
        public event EventHandler<SessionStateChangedEventArgs>? StateChanged { add { } remove { } }
        public event Action<byte[]>? DataReceived { add { } remove { } }
        public StringBuilder Sent { get; } = new();
        public void Send(ReadOnlySpan<byte> data) => Sent.Append(Encoding.UTF8.GetString(data));
        public void Resize(int columns, int rows) { }
        public Task DisconnectAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [AvaloniaFact]
    public void In_a_terminal_ctrl_k_goes_to_the_shell_and_ctrl_shift_k_opens_quick_connect()
    {
        var location = new DataLocation(StorageMode.Portable, Path.Combine(_dir, "data"), Path.Combine(_dir, "cache"), Path.Combine(_dir, "logs"));
        Directory.CreateDirectory(location.DataDir);
        VaultAutoLockTests.AddMainWindowResources();
        var window = new MainWindow(location) { Width = 1400, Height = 900 };
        try
        {
            window.Show();
            var session = new ShellSession();
            typeof(MainWindow).GetMethod("AddTab", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [new TerminalView(session), session, new SessionOrigin("box", Protocol.Ssh, null, () => Task.CompletedTask)]);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var terminal = window.GetVisualDescendants().OfType<TerminalView>().Single();
            terminal.Focus();
            var dimmer = VaultAutoLockTests.Private<Border>(window, "_dimmer");

            // Delete to the end of the line, in the shell.
            window.KeyPress(Key.K, RawInputModifiers.Control, PhysicalKey.K, null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("\u000B", session.Sent.ToString());
            Assert.False(dimmer.IsVisible);

            window.KeyPress(Key.K, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.K, null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(dimmer.IsVisible);
            Assert.Equal("\u000B", session.Sent.ToString());
        }
        finally
        {
            foreach (var palette in window.OwnedWindows.OfType<QuickConnectWindow>().ToList())
            {
                palette.Close();
            }

            Dispatcher.UIThread.RunJobs();
            window.Close();
        }
    }

    [AvaloniaFact]
    public void In_a_popped_out_terminal_too_ctrl_k_goes_to_the_shell_and_ctrl_shift_k_opens_quick_connect()
    {
        var location = new DataLocation(StorageMode.Portable, Path.Combine(_dir, "data"), Path.Combine(_dir, "cache"), Path.Combine(_dir, "logs"));
        Directory.CreateDirectory(location.DataDir);
        VaultAutoLockTests.AddMainWindowResources();
        var window = new MainWindow(location) { Width = 1400, Height = 900 };
        try
        {
            window.Show();
            var session = new ShellSession();
            typeof(MainWindow).GetMethod("AddTab", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [new TerminalView(session), session, new SessionOrigin("box", Protocol.Ssh, null, () => Task.CompletedTask)]);
            Dispatcher.UIThread.RunJobs();
            var tab = VaultAutoLockTests.Private<List<SessionTab>>(window, "_tabs").Single();
            var popOut = typeof(MainWindow).GetMethod("PopOut", BindingFlags.Instance | BindingFlags.NonPublic)!;
            popOut.Invoke(window, [tab, .. Enumerable.Repeat(Type.Missing, popOut.GetParameters().Length - 1)]);
            Dispatcher.UIThread.RunJobs();
            var popout = Assert.IsType<PopoutWindow>(tab.Popout);
            popout.UpdateLayout();
            popout.GetVisualDescendants().OfType<TerminalView>().Single().Focus();
            var dimmer = VaultAutoLockTests.Private<Border>(window, "_dimmer");

            popout.KeyPress(Key.K, RawInputModifiers.Control, PhysicalKey.K, null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("", session.Sent.ToString());
            Assert.False(dimmer.IsVisible);

            popout.KeyPress(Key.K, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.K, null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(dimmer.IsVisible);
            Assert.Single(window.OwnedWindows.OfType<QuickConnectWindow>());
            Assert.Equal("", session.Sent.ToString());
        }
        finally
        {
            foreach (var palette in window.OwnedWindows.OfType<QuickConnectWindow>().ToList())
            {
                palette.Close();
            }

            Dispatcher.UIThread.RunJobs();
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
