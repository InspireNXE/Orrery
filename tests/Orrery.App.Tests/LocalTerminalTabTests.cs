using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Orrery.App.Sessions;
using Orrery.Core.Sessions;
using Orrery.Core.Settings;
using Orrery.Core.Storage;
using Orrery.Protocols.Local;
using Xunit;

namespace Orrery.App.Tests;

/// <summary>The main window opening real local shells from the keyboard, and ending them when their tabs close.</summary>
public class LocalTerminalTabTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));

    [AvaloniaFact]
    public async Task Ctrl_shift_t_opens_the_chosen_shell_in_a_tab_and_closing_the_tab_ends_it()
    {
        var location = new DataLocation(StorageMode.Portable, Path.Combine(_dir, "data"), Path.Combine(_dir, "cache"), Path.Combine(_dir, "logs"));
        Directory.CreateDirectory(location.DataDir);
        if (OperatingSystem.IsWindows())
        {
            AppSettingsStore.In(location).Save(new AppSettings { LocalShell = LocalShells.Cmd }); // The quickest to start.
        }

        VaultAutoLockTests.AddMainWindowResources();
        var window = new MainWindow(location) { Width = 1400, Height = 900 };
        var processes = new List<Process>();
        var tabs = VaultAutoLockTests.Private<List<SessionTab>>(window, "_tabs");
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            window.KeyPressQwerty(PhysicalKey.T, OperatingSystem.IsMacOS() ? RawInputModifiers.Meta | RawInputModifiers.Shift : RawInputModifiers.Control | RawInputModifiers.Shift);
            var tab = Assert.Single(tabs);
            var session = Assert.IsType<LocalTerminalSession>(tab.Session);
            Assert.IsType<TerminalView>(tab.View);
            Assert.Equal(LocalShells.Find(AppSettingsStore.In(location).Load().LocalShell).Name, tab.Origin.Title);
            Assert.Equal("LOCAL", tab.Origin.Label);

            await UntilAsync(() => session.State == SessionState.Connected);
            processes.Add(Held(session.ProcessId!.Value));
            // The status line follows the session's state on a later UI-thread job.
            var detail = VaultAutoLockTests.Private<TextBlock>(window, "_sessionDetail");
            await UntilAsync(() => detail.Text == "LOCAL · Running");
            Assert.Empty(Orrery.Core.Connections.RecentConnections.In(location).Items); // Not a connection to come back to.

            // Again from inside the terminal, which would otherwise take it as Ctrl+T.
            await UntilAsync(() => tab.View.IsFocused);
            window.KeyPressQwerty(PhysicalKey.T, OperatingSystem.IsMacOS() ? RawInputModifiers.Meta | RawInputModifiers.Shift : RawInputModifiers.Control | RawInputModifiers.Shift);
            Assert.Equal(2, tabs.Count);
            var second = (LocalTerminalSession)tabs[1].Session;
            await UntilAsync(() => second.State == SessionState.Connected);
            processes.Add(Held(second.ProcessId!.Value));

            var close = typeof(MainWindow).GetMethod("CloseTabAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await (Task)close.Invoke(window, [session])!;
            await (Task)close.Invoke(window, [second])!;

            Assert.Empty(tabs);
            await UntilAsync(() => processes.All(p => p.HasExited));
        }
        finally
        {
            foreach (var tab in tabs.ToList())
            {
                await tab.Session.DisposeAsync();
            }

            window.Close();
            foreach (var process in processes)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                process.Dispose();
            }
        }
    }

    /// <summary>The process, held by handle on Windows so the cleanup can't reach another process that reused its id.</summary>
    static Process Held(int id)
    {
        var process = Process.GetProcessById(id);
        if (OperatingSystem.IsWindows())
        {
            _ = process.Handle;
        }

        return process;
    }

    static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50);
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
