# Packet 06: Read SSH output from the stream instead of letting it pile up, write input off the UI thread, and strip control characters from failure text

Rating: Guided. Findings: S-5, S-24, S-22. Depends on: nothing.
Runner: `packet-opus-medium` (Opus, medium effort). Threading changes inside a session class used by every SSH tab.

## Rules
- Work in your worktree branch, based on `main`. Stage only files you changed, by explicit path; one commit.
- SSH.NET 2026.0.0 is in the NuGet cache (`%USERPROFILE%\.nuget\packages\ssh.net\2026.0.0`). Read its XML docs
  for `ShellStream`; don't rely on behavior you haven't confirmed there or in a test.
- Gate: `dotnet build Orrery.sln` with 0 errors and 0 warnings; `dotnet test tests/Orrery.Protocols.Ssh.Tests` and
  `dotnet test tests/Orrery.App.Tests` while working; `dotnet test Orrery.sln` green at the end. New regression tests
  must be shown failing before the fix.
- Final report: files changed, tests added, build and test summary, Verify evidence, other notes.

## Why
- **S-5.** `SshSession.Run` (`src/Orrery.Protocols.Ssh/SshSession.cs:90-93`) only subscribes to
  `ShellStream.DataReceived`. SSH.NET copies every packet into the stream's internal read buffer before raising that
  event, and only `Read`, `ReadLine` and `Expect` drain it. Orrery calls none of them, and the channel window is re-opened
  regardless, so every byte any host sends stays in memory: up to about 2 GiB per session, after which an out-of-memory
  error inside SSH.NET's handler silently stops output. Two verifiers confirmed this by pushing 256 MiB through a real
  `ShellStream`.
- **S-24.** `Send` (`:194-212`) calls `shell.Write` and `Flush` on the caller's thread, which is the UI thread for keys,
  paste and mouse (`src/Orrery.App/Sessions/TerminalView.cs:392-419, 463-473, 512, 640, 708`). A logged-in server that
  stops opening its channel window freezes the window for up to 20 s (`ConnectionInfo.Timeout`); one that stops reading
  TCP can block it indefinitely once socket buffers fill, because SSH.NET's `socket.Send` has no timeout. Terminal replies
  call `Send` from SSH.NET's own receive thread (`TerminalView.cs:73`), which is the thread that would process the
  window adjust.
- **S-22.** A server's SSH_MSG_DISCONNECT description (sendable before key exchange) and an agent's key comment
  (`src/Orrery.Protocols.Ssh/Agent/AgentKeySource.cs:43-45`) reach `Fail(message)` and then
  `TerminalView.ShowState` (`TerminalView.cs:121-135`), which feeds `"Connection failed: {message}"` straight into the
  emulator. The verifier drew a fake "Connected." and "Password:" this way. Telnet uses the same `ShowState`.

## Changes
1. S-5: stop using `DataReceived` for terminal output. Start a dedicated reader (a long-running task or thread) after
   `CreateShellStream` that loops `shell.Read(buffer)` into a reused buffer and raises our `DataReceived` with a copy of
   what was read; `Read` returning 0 or throwing `ObjectDisposedException` means closed. Keep the existing `Closed` and
   `ErrorOccurred` handling. Confirm from the SSH.NET docs that `Read` blocks until data or close, and that it's safe to
   call from one thread while `Write` runs on another.
2. S-24: put outgoing data on a bounded queue drained by one background writer (a `Channel<byte[]>` with a capacity of a
   few hundred items, or a byte budget). `Send` copies the span and enqueues without blocking. If the queue is full, fail
   the session with "The server stopped accepting input." `Resize` can stay as it is (it isn't flow-controlled).
   Writer exceptions go to `Fail` as `Send`'s do today. Make sure `DisposeAsync` stops the writer and reader.
3. S-22: in `TerminalView.ShowState`, replace C0 controls (except none: the message is one line), DEL and C1
   (U+0080-U+009F) in `message` with a visible placeholder or a space, and cap it at a few hundred characters. Start the
   status lines with `ESC[0m` so a host's SGR 8 can't hide them. Keep the existing colors.
4. While in `SshSession.cs`, check whether the `SshLogin` record's generated `ToString` (which would print Password,
   PrivateKey and Passphrase) is used anywhere. If not, override it to omit the secrets, matching `Credential.ToString`.
5. Tests:
   - `tests/Orrery.Protocols.Ssh.Tests`: there's no SSH server fake. If you can drive `ShellStream` without a server (the
     verifiers used reflection on `Channel_DataReceived`; don't depend on private members in a committed test), write a
     test that feeds several MiB and checks the stream's `Length` stays small. If you can't without private API, add a
     focused unit test for the reader loop over a stand-in `Stream`, and report that the end-to-end check wasn't automated.
   - Queue: `Send` returns immediately when the writer is blocked; a full queue fails the session.
   - `tests/Orrery.App.Tests`: `ShowState(Failed, "x\u001b[2Jy\u009b")` feeds no escape sequence (assert on the
     emulator's text and that the screen wasn't cleared).

Write your plan (files, the reader and writer shape, how `Send`'s callers are affected, test approach) and stop for
review before editing.

## Verify
- The tests above pass and fail on `main` where they can.
- `dotnet test Orrery.sln` green.
- Note in the report that a live check against a real SSH server is still needed (`yes | head -c 2G` should leave memory flat).

## Do not touch
- The SSH agent code (packet 13) and the algorithm lists (packet 14), even though both are in or next to `SshSession.cs`.
- Focus handling in `ShowState` (packet 16).
