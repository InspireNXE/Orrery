# Packet 09: Stop a session's background failure from crashing the whole app

Rating: Mechanical. Findings: S-15, S-16, and the round's cross-cutting note (no unhandled-exception backstop).
Depends on: 01 (both edit `RemoteFrameView.cs` and `RdpSession.cs`).
Runner: `packet-sonnet-medium` (Sonnet, medium effort).

## Rules
- Work in your worktree branch, based on `main` after packet 01 has merged. Stage only files you changed, by explicit
  path; one commit.
- Gate: `dotnet build Orrery.sln` with 0 errors and 0 warnings; `dotnet test tests/Orrery.App.Tests` while working;
  `dotnet test Orrery.sln` green at the end. New regression tests must be shown failing before the fix.
- Final report: files changed, tests added, build and test summary, Verify evidence, other notes.

## Why
Nothing in `src/Orrery.App/Program.cs` or `App.axaml.cs` subscribes to `Dispatcher.UIThread.UnhandledException`,
`AppDomain.CurrentDomain.UnhandledException` or `TaskScheduler.UnobservedTaskException`. In Avalonia 11.3.22 an
exception that escapes an `async void` handler on the dispatcher then ends the process, with every open session.

- **S-15.** The VNC receive handler (`src/Orrery.App/Sessions/RemoteFrameView.cs:79-89`) posts an `async` lambda that
  awaits `clipboard.SetTextAsync(text)` with no catch. When another process holds the Windows clipboard open, that
  throws, and a VNC server can send cut text as often as it likes, even while Orrery is in the background. The send
  path (`:384-401`) already catches `COMException` and `InvalidOperationException` for exactly this.
- **S-16.** `RdpSession.ClipboardLoopAsync` (`src/Orrery.Protocols.Rdp/RdpSession.cs:137-167`) catches only
  cancellation and `IronRdpException`. A write to a dropped connection throws `IOException` and faults
  `_clipboardLoop`. `DisconnectAsync` (`:427-451`) then awaits it outside its try, and the exception reaches the `async
  void` close and disconnect handlers in `MainWindow.axaml.cs` (`:1106, 1238, 1317-1322`).

## Changes
1. S-15: wrap the `SetTextAsync` call in the same catch the send path uses, with the same trace warning.
2. S-16: in `ClipboardLoopAsync`, add a catch for any other exception that traces it and ends the loop (the session's
   read loop reports the disconnect). In `DisconnectAsync`, await `_readLoop` and `_clipboardLoop` inside try blocks that
   trace and swallow, since each loop has already reported its own outcome.
3. Backstop: in `App.OnFrameworkInitializationCompleted` (`App.axaml.cs:25`), subscribe to
   `Dispatcher.UIThread.UnhandledException`. Trace the exception in full, mark it handled, and show one error message
   (reuse `Dialogs` if there's a suitable helper; look in `src/Orrery.App` first) that says something went wrong and
   that open sessions may need reconnecting. Also subscribe to `TaskScheduler.UnobservedTaskException` and trace and
   observe. Don't swallow `AppDomain` unhandled exceptions; those can't be recovered. Add a short comment saying this is a
   backstop and that each session should still contain its own failures.
4. Tests in `tests/Orrery.App.Tests` (Avalonia.Headless, see `TestApp.cs` and `RemoteFrameViewTests.cs` for set-up):
   - a fake `IClipboardSession` raising `RemoteClipboardChanged` while the headless clipboard throws doesn't reach the
     dispatcher's unhandled handler (subscribe in the test and assert it wasn't called). If the headless clipboard can't
     be made to throw, say so and test the catch through a seam you add, kept small.
   - There are no RDP tests and no RDP fake. Don't build one here; report S-16 as covered by code review only.

## Verify
- The VNC clipboard test fails on `main` and passes after.
- `dotnet test Orrery.sln` green.

## Do not touch
- Desktop size checks in `RdpSession` and `RemoteFrameView.Flush` (packet 01).
- Focus handling in `RemoteFrameView.ShowState` (packet 16).
- Whether VNC clipboard sharing should be on by default: the verifier rejected changing it.
