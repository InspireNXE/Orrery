# Packet 13: Only talk to an SSH agent we can trust, never hang on one, and handle its bad replies cleanly

Rating: Guided. Findings: S-25, S-26, S-27, S-28. Depends on: 06 (both edit `SshSession.cs`).
Runner: `packet-opus-high` (Opus, high effort). Windows interop around the code that asks for signatures with the user's keys.

## Rules
- Work in your worktree branch, based on `main` after packet 06 has merged. Stage only files you changed, by explicit
  path; one commit.
- Existing agent tests: `tests/Orrery.Protocols.Ssh.Tests/SshAgentTests.cs` with `FakeAgent.cs` and `FakePageant.cs`.
  Extend the fakes rather than writing new ones.
- Gate: `dotnet build Orrery.sln` with 0 errors and 0 warnings; `dotnet test tests/Orrery.Protocols.Ssh.Tests` while
  working; `dotnet test Orrery.sln` green at the end. New regression tests must be shown failing before the fix.
- Final report: files changed, tests added, build and test summary, Verify evidence, other notes.

## Why
- **S-25.** `SshAgentClient.Find` (`src/Orrery.Protocols.Ssh/Agent/SshAgentClient.cs:32-53`) picks
  `\\.\pipe\openssh-ssh-agent` when `File.Exists` is true, before trying Pageant. On Windows the OpenSSH agent service is
  stopped and disabled by default, so another user or a low-integrity process can create that pipe first. `OpenPipe`
  (`:133-146`) uses the three-argument `NamedPipeClientStream`, which sends no SQOS flags, so the pipe server gets an
  impersonation-level token (a probe confirmed it; passing `TokenImpersonationLevel.Identification` gives identification
  level). Nothing checks who runs the server. The squatter can block Pageant, return fake keys, hang connects, read the
  username and session ID from sign requests, and, with SeImpersonatePrivilege, act as the user. The same code serves
  `SSH_AUTH_SOCK` when it names a pipe (`:115-118`).
- **S-26.** `WireReader.String` (`:210-217`) checks `_position + length > data.Length`, which wraps for a length near
  `int.MaxValue` and throws `ArgumentOutOfRangeException` instead of `IOException`. `Sign` can also throw
  `Win32Exception` (`PageantTransport.cs:46-59`) or `UnauthorizedAccessException`. `SshSession.Run`'s filter
  (`SshSession.cs:113`) doesn't list those two, and `Start` discards the task (`:53`), so the tab sits at "Connecting…".
- **S-27.** Pageant's `SendMessage(WM_COPYDATA)` (`PageantTransport.cs:69`) has no timeout and runs inside a static
  `lock (Gate)` (`:36`), so one hung or fake "Pageant" window blocks every later Pageant request for the life of the
  process. `StreamTransport.Exchange` (`SshAgentClient.cs:151-169`) has no read timeout, and the identity listing runs
  before SSH.NET's connect timeout applies (`SshSession.cs:142, 176-192`). `FindWindow` (`:25-38`) doesn't check who owns
  the window. A probe showed a low-integrity window blocking `SendMessage`, and a silent pipe holding `ListIdentities`.
- **S-28 (plausible).** The shared-memory name is `PageantRequest{GetCurrentThreadId():x8}` (`:44`), which is
  predictable, and `CreateFileMapping`'s result is used without checking `ERROR_ALREADY_EXISTS`. A probe showed a
  low-integrity process pre-creating the section and reading Orrery's sign request from it. Whether Pageant would then
  accept that section wasn't tested.

## Changes
1. S-25:
   - Open every agent pipe with `TokenImpersonationLevel.Identification`.
   - Before sending anything on a pipe, get the server process with `GetNamedPipeServerProcessId` (P/Invoke, on the
     pipe's `SafePipeHandle`), open it with `PROCESS_QUERY_LIMITED_INFORMATION`, and check that its token user is the
     current user or SYSTEM (`S-1-5-18`). Otherwise throw `IOException("The SSH agent pipe belongs to another account.")`.
   - In `Find`, don't treat the pipe's existence alone as "the agent is here": if the pipe check fails, fall through to
     Pageant. `File.Exists` on a pipe also opens a connection to it; replace it with a check that doesn't send data (or
     accept that it connects, and say so).
2. S-26: in `WireReader`, compare with `length > data.Length - _position` (and reject negative lengths). Wrap
   everything the sign path can throw (`Win32Exception`, `UnauthorizedAccessException`, `ArgumentException` from the
   reader) in `IOException` inside `AgentHostAlgorithm.Sign` (or wherever `AgentKeySource` signs), so `Run`'s existing
   filter handles it. Check how SSH.NET surfaces an exception from a host algorithm's `Sign` during authentication before
   deciding; the goal is that the session fails with a message instead of hanging.
3. S-27: use `SendMessageTimeout` with `SMTO_ABORTIFHUNG` and a timeout of a few seconds instead of `SendMessage`, and
   give `StreamTransport` a read timeout (pipes and sockets: use `ReadTimeout` where supported, otherwise a cancellation
   token with `ReadExactlyAsync`). Check the Pageant window's owner with `GetWindowThreadProcessId` and the same token-user
   check as step 1 (current user only; Pageant never runs as SYSTEM).
4. S-28: create the mapping with a random name (`PageantRequest` plus a random 16-hex-digit suffix; Pageant reads the
   name from the message, so any name works; confirm that against PuTTY's documented protocol, or say it's unconfirmed),
   and fail with `IOException` if `GetLastWin32Error()` is `ERROR_ALREADY_EXISTS` (183) right after `CreateFileMapping`.
   If you find that current Pageant (0.75 and later) offers a per-user named pipe that PuTTY prefers, report it as a
   follow-up; don't switch transports in this packet.
5. Tests in `SshAgentTests.cs`:
   - `WireReader` with a length of `0x7FFFFFF8` at position 8 throws `IOException`.
   - a fake agent that never replies makes `ListIdentities` fail within the timeout (keep the test's timeout short via a
     constructor parameter or internal setting).
   - `FakePageant` that hangs in its message handler: the call times out, and a second call after it isn't blocked forever.
   - the owner check: a pipe served by the current process passes. A pipe owned by another account can't be created in a
     unit test; say so and leave that path covered by review.

Write your plan (P/Invoke signatures, where the owner check lives, timeout values, test approach) and stop for review
before editing.

## Verify
- The new tests fail on `main` where they can, and pass after.
- `dotnet test Orrery.sln` green.
- Lower-integrity processes of the same user are in scope (owner decision 2026-10-30), so S-28 is High. The probes
  for S-25, S-27 and S-28 should use a low-integrity child process as the attacker where they can.

## Do not touch
- SSH.NET's algorithm lists (packet 14) and the stream reader and writer (packet 06).
- Linux and macOS `SSH_AUTH_SOCK` sockets: same-user by design, out of scope.
