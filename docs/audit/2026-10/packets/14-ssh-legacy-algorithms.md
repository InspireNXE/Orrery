# Packet 14: Drop SHA-1, CBC and 3DES from SSH connections unless a connection allows legacy algorithms

Rating: Mechanical. Findings: S-23. Depends on: 13 (both edit `SshSession.cs`; 13 itself follows 06).
Runner: `packet-sonnet-medium` (Sonnet, medium effort).

## Rules
- Work in your worktree branch, based on `main` after packet 13 has merged. Stage only files you changed, by explicit
  path; one commit.
- Follow how `ShareClipboard` and `UseSshAgent` work end to end: `ConnectionNode` (`src/Orrery.Core/Connections/ConnectionNode.cs:48-51`),
  `SettingsResolver` (`SettingsResolver.cs:15-16, 38-39`), the inspector (`src/Orrery.App/Connections/PropertiesPanel.cs:32, 61, 192-193, 254`),
  and where the resolved value reaches the session in `MainWindow.axaml.cs`. A new setting must inherit from folders the same way.
- Gate: `dotnet build Orrery.sln` with 0 errors and 0 warnings; focused tests while working; `dotnet test Orrery.sln`
  green at the end. New regression tests must be shown failing before the fix.
- Final report: files changed, tests added, build and test summary, Verify evidence, other notes.

## Why
`SshSession.Run` builds `new ConnectionInfo(...)` (`src/Orrery.Protocols.Ssh/SshSession.cs:61-64`) and keeps SSH.NET
2026.0.0's default lists, which still include diffie-hellman-group1-sha1, group14-sha1 and group-exchange-sha1, 3des-cbc
and the aes-cbc ciphers, hmac-sha1 and hmac-sha1-etm, and the ssh-rsa (SHA-1) host key algorithm. They're listed last,
and the signed exchange hash stops a man in the middle from downgrading a modern server, so this is hardening: a legacy
server that only speaks these gets a weak connection with no warning, and strict KEX (Terrapin protection) is only used
when the server offers it.

## Changes
1. Add `bool? AllowLegacySsh` to `ConnectionNode`, resolved by `SettingsResolver` with a default of false, and a toggle
   in the inspector's SSH section labeled "Allow older, weaker algorithms", with a one-line hint ("Only for old devices
   that can't connect otherwise"). Mirror `UseSshAgent` exactly, including inheritance and the JSON name.
2. Pass it to `SshSession.Start`, and when it's false, remove from the `ConnectionInfo` dictionaries
   (`KeyExchangeAlgorithms`, `Encryptions`, `HmacAlgorithms`, `HostKeyAlgorithms`): `diffie-hellman-group1-sha1`,
   `diffie-hellman-group14-sha1`, `diffie-hellman-group-exchange-sha1`, `3des-cbc`, `aes128-cbc`, `aes192-cbc`,
   `aes256-cbc`, `hmac-sha1`, `hmac-sha1-etm@openssh.com`, and `ssh-rsa` as a host key algorithm. Keep the rsa-sha2
   variants. Check the exact key names in SSH.NET's `ConnectionInfo` before removing; a name that doesn't exist must
   fail the test, not be silently skipped.
3. When a connection fails on algorithm negotiation and legacy is off, make the failure message say the server may
   need "Allow older, weaker algorithms". Find how SSH.NET reports no matching algorithm (an `SshConnectionException`
   message) and match it narrowly.
4. Tests:
   - `SettingsResolver` resolves the new setting with inheritance (follow the existing resolver tests).
   - A unit test of the function that builds the algorithm lists (extract one if needed): with legacy off none of the
     names above are present and the modern ones (curve25519-sha256, aes256-gcm@openssh.com, hmac-sha2-256-etm@openssh.com,
     ssh-ed25519, rsa-sha2-512) are; with legacy on, the lists are SSH.NET's defaults.
   - The inspector test pattern in `tests/Orrery.App.Tests/PropertiesPanelTests.cs` for the new toggle.

## Verify
- The new tests fail on `main` and pass after.
- `dotnet test Orrery.sln` green.

## Do not touch
- Agent code (packet 13) and the stream reader and writer (packet 06).
