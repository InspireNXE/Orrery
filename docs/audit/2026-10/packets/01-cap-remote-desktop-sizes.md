# Packet 01: Cap the desktop size a VNC or RDP server can announce, and size buffers in 64-bit arithmetic

Rating: Mechanical. Findings: S-1, S-6 (and the in-our-hands part of S-7). Depends on: nothing.
Runner: `packet-opus-high` (Opus, high effort). S-1 is remote heap corruption; a clamp in the wrong place reopens it.

## Rules
- Work in your worktree branch, based on `main`. Stage only files you changed, by explicit path; one commit.
- No CLAUDE.md exists. Match the surrounding code: file-scoped namespaces, short XML doc comments that say why.
- Gate: `dotnet build Orrery.sln` with 0 errors and 0 warnings; while working,
  `dotnet test tests/Orrery.Protocols.Vnc.Tests` and `dotnet test tests/Orrery.App.Tests --filter "FullyQualifiedName~RemoteFrameView"`;
  `dotnet test Orrery.sln` green at the end (baseline 250 tests). New regression tests must be shown failing before the fix.
- Final report: files changed, tests added, build and test summary, Verify evidence, other notes.

## Why
A VNC server picks the framebuffer size (two u16s). `VncSession.GrabFramebufferReference`
(`src/Orrery.Protocols.Vnc/VncSession.cs:145-165`) allocates `size.Width * size.Height * BytesPerPixel` computed in
`int`, so 32768x32768x4 wraps to 0. `NativeMemory.AllocZeroed` then returns a tiny block, but `_bufferWidth` and
`_bufferHeight` get the full size and `FrameReference.Size` (`:219-237`) reports it back to the library. The library's
`FramebufferCursor` uses that size for its stride and writes server pixels far past the allocation. The library caps
nothing itself. This is the round's only Critical.

RDP has the same shape without the memory corruption. `RdpSession` passes the server's `DesktopSize` (ushort x ushort)
straight to `ResetImage` (`src/Orrery.Protocols.Rdp/RdpSession.cs:89-90` at connect, `:242` on reactivation,
`:259-268`). `ResetImage` allocates the native `DecodedImage`, then `new byte[width * height * BytesPerPixel]` in
`int` (wraps or throws), and assigns `Width`/`Height` before allocating. `RemoteFrameView.Flush`
(`src/Orrery.App/Sessions/RemoteFrameView.cs:224-245`) then creates a `WriteableBitmap` of that size on the UI thread
with no catch. Nothing in the app catches an unhandled UI exception, so a hostile RDP server can end the process. The
only clamp today is on our own resize request (`RdpSession.RequestResize`, 200-8192 at `:366-368`).

The shipped IronRDP (2026.10.29) predates upstream's own size check (#2084), and even that accepts 32766^2, so our cap
is needed regardless.

## Changes
1. Add one shared limit for both protocols, e.g. a small static class in `src/Orrery.Core` (look for an existing
   home first; `Orrery.Core` is referenced by both protocol projects) with a `MaxSide = 8192` and a method that says
   whether a width and height are acceptable (both 1..8192). Use the same ceiling `RequestResize` uses, and have
   `RequestResize` use the constant instead of its own literal.
2. VNC: in `GrabFramebufferReference`, before `FreeBuffer()`, reject a size outside the limit by throwing an exception
   the library turns into a failed connection. Check what the library does with an exception from
   `GrabFramebufferReference` (it's in `Community.MarcusW.VncClient` 2.0.8 in the NuGet cache), and make sure the
   session ends up in `SessionState.Failed` with a plain message such as "The server asked for a 32768x32768 desktop,
   which is larger than Orrery supports." Compute the byte count as `checked((nuint)size.Width * (nuint)size.Height * BytesPerPixel)`
   or in `long`. Also check every other place in `VncSession.cs` that multiplies width, height or stride
   (`CopyRegion`, `FrameReference`, the cursor and screenshot paths) and widen them the same way.
3. RDP: validate the size at both call sites (`:89-90` and `:242`) before calling `ResetImage`. On a bad size, fail
   the session with a plain message rather than throwing out of the read loop unexplained. In `ResetImage`, compute
   in `long`/checked, allocate `_frame` and the native image first, and assign `Width`/`Height` only after both succeed.
4. `RemoteFrameView.Flush`: wrap the bitmap creation and copy in a try/catch for `ArgumentException`,
   `OutOfMemoryException` and `InvalidOperationException` that disconnects the session and shows the failure state,
   instead of letting it escape the dispatcher.
5. Tests:
   - `tests/Orrery.Protocols.Vnc.Tests/VncSessionTests.cs`: using `TestRfbServer`, a server announcing 32768x32768
     (and one announcing 8193x10) ends in `Failed`, and the test process is still alive. A server announcing 1920x1080
     still connects. Extend `TestRfbServer` if it can't announce a chosen size.
   - Unit tests for the shared limit (0, 1, 8192, 8193, 65535).
   - There are no RDP tests. If `ResetImage` can be reached without a server (it's private), test the size check
     through the shared helper only and say so in the report. Don't build an RDP test server in this packet.

## Verify
- The new VNC tests pass, and fail on `main` (the 32768 case should crash or corrupt the test host on `main`; if it
  takes the test run down, show that output as the "failing first" evidence and say so).
- `dotnet test Orrery.sln` green.

## Do not touch
- The VNC security types and `Authenticator` (packet 02).
- The RDP and VNC clipboard code (packet 09), `RdpConnector` (packet 15), and focus handling in `RemoteFrameView` (packet 16).
- `IronRdpVersion` in `Directory.Build.props` (packet 18).
