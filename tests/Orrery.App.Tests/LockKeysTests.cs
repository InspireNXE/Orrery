using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Orrery.App.Sessions;
using Orrery.Core.Sessions;
using Orrery.Protocols.Rdp;
using Xunit;

namespace Orrery.App.Tests;

/// <summary>Caps Lock and Num Lock on a remote desktop: matched to this computer's when it gets the keyboard, as mstsc does.</summary>
public class LockKeysTests
{
    class Desktop : IFrameBufferSession
    {
        public List<(ushort Code, bool Pressed)> Keys { get; } = [];
        public ConnectionTarget Target { get; } = new("desk.example", 3389, "user", null);
        public SessionState State => SessionState.Connected;
        public event EventHandler<SessionStateChangedEventArgs>? StateChanged { add { } remove { } }
        public event EventHandler<FrameUpdatedEventArgs>? FrameUpdated { add { } remove { } }
        public int Width => 64;
        public int Height => 48;
        public void CopyRegion(FrameRect region, IntPtr destination, int destinationStride) { }
        public void SendKey(ushort scancode, bool pressed) => Keys.Add((scancode, pressed));
        public void ReleaseAllKeys() { }
        public void SendPointerMove(int x, int y) { }
        public void SendPointerButton(PointerButton button, bool pressed) { }
        public void SendWheel(int units, bool horizontal) { }
        public void RequestResize(int width, int height) { }
        public Task DisconnectAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    sealed class SyncingDesktop : Desktop, ILockKeysSession
    {
        public List<LockKeys> Synced { get; } = [];
        public void SyncLockKeys(LockKeys keys) => Synced.Add(keys);
    }

    static (RemoteFrameView View, Window Window) Show(IFrameBufferSession session, Func<LockKeys?> local)
    {
        var view = new RemoteFrameView(session) { ReadLockKeys = local };
        var window = new Window { Content = view, Width = 400, Height = 300 };
        window.Show();
        view.Focus();
        Dispatcher.UIThread.RunJobs();
        return (view, window);
    }

    [Fact]
    public void Rdp_sets_them_with_one_fast_path_synchronize_event()
    {
        // Header: action 0, one event. Length 3. Event code 3 (synchronize) in the top bits, the keys in the bottom.
        Assert.Equal(new byte[] { 0x04, 0x03, 0x66 }, RdpSession.SynchronizePdu(LockKeys.CapsLock | LockKeys.NumLock));
        Assert.Equal(new byte[] { 0x04, 0x03, 0x60 }, RdpSession.SynchronizePdu(LockKeys.None));
        Assert.Equal(new byte[] { 0x04, 0x03, 0x61 }, RdpSession.SynchronizePdu(LockKeys.ScrollLock));
    }

    [AvaloniaFact]
    public void Getting_the_keyboard_matches_the_remote_to_this_computer()
    {
        var session = new SyncingDesktop();
        var local = LockKeys.CapsLock;
        var (view, window) = Show(session, () => local);
        // Showing the window can match more than once (connected, focused, activated), always to the same state.
        Assert.NotEmpty(session.Synced);
        Assert.All(session.Synced, keys => Assert.Equal(LockKeys.CapsLock, keys));

        // Caps Lock turned off while another window had the keyboard: matched again on the way back.
        local = LockKeys.None;
        view.MatchLockKeys();
        Assert.Equal(LockKeys.None, session.Synced[^1]);
        window.Close();
    }

    [AvaloniaFact]
    public void Num_lock_from_send_keys_stays_on_at_the_remote_only()
    {
        var session = new SyncingDesktop();
        var (view, window) = Show(session, () => LockKeys.CapsLock);
        session.Synced.Clear();

        view.SendKeys(RemoteKeyCombo.Menu.Single(c => c.Label == "Num Lock"));
        Assert.Equal(LockKeys.CapsLock | LockKeys.NumLock, Assert.Single(session.Synced));
        Assert.Empty(session.Keys); // Set outright, not pressed.

        // Getting the keyboard again keeps it, with this computer's Caps Lock.
        view.MatchLockKeys();
        Assert.Equal(LockKeys.CapsLock | LockKeys.NumLock, session.Synced[^1]);

        // And Send keys turns it back off.
        view.SendKeys(RemoteKeyCombo.Menu.Single(c => c.Label == "Num Lock"));
        Assert.Equal(LockKeys.CapsLock, session.Synced[^1]);
        window.Close();
    }

    [AvaloniaFact]
    public void Where_they_cant_be_set_or_read_send_keys_presses_the_key()
    {
        // A session that can't set them (VNC), and an RDP one where this computer's can't be read (Linux).
        foreach (var (session, local) in new (Desktop, LockKeys?)[] { (new Desktop(), LockKeys.None), (new SyncingDesktop(), null) })
        {
            var (view, window) = Show(session, () => local);
            view.SendKeys(RemoteKeyCombo.Menu.Single(c => c.Label == "Caps Lock"));
            Assert.Equal([(0x3A, true), (0x3A, false)], session.Keys);
            Assert.Empty((session as SyncingDesktop)?.Synced ?? []);
            window.Close();
        }
    }
}
