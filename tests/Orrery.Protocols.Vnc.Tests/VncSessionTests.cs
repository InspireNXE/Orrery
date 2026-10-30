using System.Runtime.InteropServices;
using Orrery.Core.Sessions;
using Xunit;

namespace Orrery.Protocols.Vnc.Tests;

public class VncSessionTests
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    static async Task<VncSession> ConnectAsync(TestRfbServer server, string password)
    {
        var frame = new TaskCompletionSource();
        var session = VncSession.Start(new ConnectionTarget("127.0.0.1", server.Port, null, null), password);
        session.FrameUpdated += (_, _) => frame.TrySetResult();
        session.StateChanged += (_, e) =>
        {
            if (e.State == SessionState.Failed) frame.TrySetException(new Exception(e.Message));
        };

        await frame.Task.WaitAsync(Wait);
        return session;
    }

    static async Task WaitUntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow + Wait;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "timed out");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Logs_in_with_the_vnc_password_and_shows_the_remote_picture()
    {
        await using var server = new TestRfbServer(password: "dummypw1");
        await using var session = await ConnectAsync(server, "dummypw1");

        Assert.Equal(SessionState.Connected, session.State);
        Assert.Equal((TestRfbServer.Width, TestRfbServer.Height), (session.Width, session.Height));
        Assert.Contains("security 2", server.Log);

        var rgba = new byte[TestRfbServer.Width * TestRfbServer.Height * 4];
        var handle = GCHandle.Alloc(rgba, GCHandleType.Pinned);
        try
        {
            await WaitUntilAsync(() =>
            {
                session.CopyRegion(new FrameRect(0, 0, session.Width, session.Height), handle.AddrOfPinnedObject(), TestRfbServer.Width * 4);
                return rgba[(7 * TestRfbServer.Width + 5) * 4 + 1] == 255;
            });
        }
        finally
        {
            handle.Free();
        }

        foreach (var (x, y) in new[] { (0, 0), (5, 7), (40, 20), (63, 47) })
        {
            int i = (y * TestRfbServer.Width + x) * 4;
            Assert.Equal(TestRfbServer.PixelAt(x, y), (rgba[i], rgba[i + 1], rgba[i + 2]));
        }
    }

    [Fact]
    public async Task Logs_in_to_a_server_without_a_password()
    {
        await using var server = new TestRfbServer(password: null);
        await using var session = await ConnectAsync(server, string.Empty);

        Assert.Equal(SessionState.Connected, session.State);
        Assert.Contains("security 1", server.Log);
    }

    [Fact]
    public async Task A_wrong_password_fails_with_a_message()
    {
        await using var server = new TestRfbServer(password: "dummypw1");
        var failed = new TaskCompletionSource<string?>();
        await using var session = VncSession.Start(new ConnectionTarget("127.0.0.1", server.Port, null, null), "wrongpw!");
        session.StateChanged += (_, e) =>
        {
            if (e.State == SessionState.Failed) failed.TrySetResult(e.Message);
        };
        if (session.State == SessionState.Failed) failed.TrySetResult("failed");

        var message = await failed.Task.WaitAsync(Wait);

        Assert.False(string.IsNullOrWhiteSpace(message));
        Assert.Contains("auth rejected", server.Log);
    }

    [Fact]
    public async Task Sends_keysyms_and_releases_held_keys()
    {
        await using var server = new TestRfbServer(password: null);
        await using var session = await ConnectAsync(server, string.Empty);

        session.SendKeysym('a', pressed: true);
        session.SendKeysym('a', pressed: false);
        session.SendKeysym(0xFFE1, pressed: true); // Shift_L, then focus is lost.
        session.ReleaseAllKeys();
        session.SendKeysym('b', pressed: false);   // Never pressed: dropped.

        await WaitUntilAsync(() => server.Keys.Count >= 4);
        await Task.Delay(100);
        Assert.Equal([(true, 'a'), (false, 'a'), (true, 0xFFE1u), (false, 0xFFE1u)], server.Keys.ToArray());
    }

    [Fact]
    public async Task Sends_pointer_moves_buttons_and_wheel_notches()
    {
        await using var server = new TestRfbServer(password: null);
        await using var session = await ConnectAsync(server, string.Empty);

        session.SendPointerMove(10, 20);
        session.SendPointerButton(PointerButton.Left, pressed: true);
        session.SendPointerButton(PointerButton.Left, pressed: false);
        session.SendWheel(60, horizontal: false);  // Half a notch: nothing yet.
        session.SendWheel(60, horizontal: false);  // Completes one notch up.

        await WaitUntilAsync(() => server.Pointer.Count >= 5);
        Assert.Equal([(0, 10, 20), (1, 10, 20), (0, 10, 20), (8, 10, 20), (0, 10, 20)],
            server.Pointer.Select(p => ((int)p.Mask, p.X, p.Y)).ToArray());
    }

    [Fact]
    public async Task Disconnecting_closes_the_connection_promptly()
    {
        await using var server = new TestRfbServer(password: null);
        var session = await ConnectAsync(server, string.Empty);

        var started = DateTime.UtcNow;
        await session.DisposeAsync();

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(1), "disconnect took too long");
        Assert.Equal(SessionState.Disconnected, session.State);
        await WaitUntilAsync(() => server.Log.Contains("client hung up"));
    }
}
