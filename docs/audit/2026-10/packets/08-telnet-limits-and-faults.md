# Packet 08: Cap Telnet subnegotiation and stop a fault in the Telnet read loop from crashing the app later

Rating: Mechanical. Findings: S-14, S-10 (Telnet part). Depends on: nothing.
Runner: `packet-sonnet-low` (Sonnet, low effort).

## Rules
- Work in your worktree branch, based on `main`. Stage only files you changed, by explicit path; one commit.
- Gate: `dotnet build Orrery.sln` with 0 errors and 0 warnings; `dotnet test tests/Orrery.Protocols.Telnet.Tests` while
  working; `dotnet test Orrery.sln` green at the end. New regression tests must be shown failing before the fix.
- Final report: files changed, tests added, build and test summary, Verify evidence, other notes.

## Why
- **S-14.** After `IAC SB <option>`, the parser appends every byte to `_sub` (`src/Orrery.Protocols.Telnet/TelnetSession.cs:244`,
  appends at `:281-295`) until `IAC SE`. It's cleared only on the next `SB` (`:271`). A server that never sends `IAC SE`
  grows memory without limit. We only answer NAWS and TTYPE subnegotiations.
- **S-10, Telnet part.** `RunAsync` (`:54-108`) invokes `DataReceived` inside its try, but the catches cover only
  cancellation, `SocketException`, `IOException` and `ObjectDisposedException` (`:95-107`). Any other exception from the
  terminal (packet 07 fixes the known ones, but there may be others) faults `_run` silently: the tab still says Connected
  and shows nothing. `DisconnectAsync` (`:202-214`) then does `await _run`, which rethrows into the `async void` close,
  disconnect and reconnect handlers in `MainWindow.axaml.cs` (`:1106, 1238, 1317-1324, 1338-1342`), and the whole app exits.
  A verifier reproduced this against a loopback server.

## Changes
1. Cap `_sub` at 4 KiB. Past the cap, discard the subnegotiation and return the parser to the data state at the next
   `IAC SE`, or fail the session with "The server sent a malformed Telnet negotiation."; choose the one that keeps the
   parser simplest and say which.
2. In `RunAsync`, add a final catch for any other exception: trace it (as the existing catch does) and set the state to
   `Failed` with a plain message, so the tab shows the failure.
3. In `DisconnectAsync`, don't rethrow from `_run`: await it inside a try that swallows (and traces) anything, since the
   read loop has already reported its own outcome.
4. Tests in `tests/Orrery.Protocols.Telnet.Tests/TelnetSessionTests.cs` with a loopback server, as the existing tests do:
   - a server sending `IAC SB 24` and then 1 MiB without `IAC SE` doesn't grow the parser beyond the cap (assert on
     behavior: the session stays responsive and later data after `IAC SE` prints, or the session fails cleanly);
   - a `DataReceived` handler that throws makes the session `Failed`, and `DisconnectAsync` and `DisposeAsync` complete
     without throwing.

## Verify
- The tests above fail on `main` and pass after.
- `dotnet test Orrery.sln` green.

## Do not touch
- `TerminalEmulator` (packet 07).
