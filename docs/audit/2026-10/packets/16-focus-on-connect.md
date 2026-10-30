# Packet 16: Don't move keyboard focus to a session that connects while the user is typing somewhere else

Rating: Mechanical. Findings: S-30. Depends on: 06 and 15 (06 edits `TerminalView.ShowState`; 01 and 09, which come before 15, edit `RemoteFrameView.cs`).
Runner: `packet-sonnet-low` (Sonnet, low effort).

## Rules
- Work in your worktree branch, based on `main` after packets 06 and 15 have merged. Stage only files you changed, by
  explicit path; one commit.
- Gate: `dotnet build Orrery.sln` with 0 errors and 0 warnings; `dotnet test tests/Orrery.App.Tests` while working;
  `dotnet test Orrery.sln` green at the end. New regression tests must be shown failing before the fix.
- Final report: files changed, tests added, build and test summary, Verify evidence, other notes.

## Why
When a session reaches Connected, `RemoteFrameView.ShowState` calls `Focus()` (`src/Orrery.App/Sessions/RemoteFrameView.cs:164-183`,
at `:180`), and `TerminalView.ShowState` posts `Focus()` (`src/Orrery.App/Sessions/TerminalView.cs:137-140`), whatever
has focus at the time. The credential editor isn't a dialog: it's hosted in the main window's right pane
(`_propertiesHost.Child = _credentialsView.Editor`, `src/Orrery.App/MainWindow.axaml.cs:482`) with password boxes
(`src/Orrery.App/Credentials/CredentialsView.cs:37, 39`). A user who opens a connection to a slow host and meanwhile types
a password there has the rest of it sent to the remote host when the session connects. A hostile server controls the
timing. The verifier rejected this on the grounds that credential prompts are modal; that's true of the prompts, not of
the editor. Not reproduced in the running app.

## Changes
1. In both views, on Connected, take focus only if focus is currently inside this session view, on nothing, or on the tab
   strip or toolbar that opened it. Use `TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement()` and check whether
   it's a `TextBox` outside the view; if it is, leave focus where it is. Put the check in one small helper both views use.
2. Tests in `tests/Orrery.App.Tests` (headless; see `RemoteFrameViewTests.cs` and `TerminalViewMouseTests.cs` for set-up):
   a window with a focused `TextBox` and a session view that then reaches Connected keeps focus on the `TextBox`;
   with nothing focused, the session view gets focus.

## Verify
- Both tests fail on `main` and pass after.
- `dotnet test Orrery.sln` green.

## Do not touch
- Anything else in `ShowState`.
