# Audit round 2026-10

Security-only round, run 2026-10-30 against `main` at `58e9c72` ("Add the audit profile"). Profile:
`.claude/audit-profile.md`. Raw tool output: `%LOCALAPPDATA%\orrery-audit\2026-10\tooling` (outside the repo).

Tag: `audit-2026-10` suggested, not created yet (waiting on the owner).

## Baseline

| Check | Result |
|---|---|
| Working tree | Clean after committing the profile |
| `dotnet build Orrery.sln` | 0 errors, 0 warnings |
| `dotnet test Orrery.sln` | 250 passed, 0 failed (Core 166, App 65, VNC 8, SSH 6, Telnet 5) |
| `dotnet list package --vulnerable --include-transitive` | No known vulnerable packages in any project (`vulnerable.txt`) |
| `dotnet list package --deprecated` | Only `xunit` 2.9.3, marked Legacy in favour of xunit.v3 (test-only) |
| `dotnet list package --outdated --include-transitive` | IronRDP 2026.10.29, SSH.NET 2026.0.0, MarcusW.VncClient 2.0.8 and NSec 26.4.0 are current. Behind: Avalonia 11.3.22 (12.1.4 out, a major version), SkiaSharp 2.88.9 and HarfBuzzSharp 8.3.1.1 (through Avalonia), BouncyCastle 2.6.2 (2.7.0), Tmds.DBus.Protocol 0.21.3 (0.95.2, through Avalonia on Linux), Newtonsoft.Json 13.0.3 (13.0.4). Full list in `outdated.txt` |
| Secrets in history | gitleaks isn't installed (`winget install Gitleaks.Gitleaks`; not installed without asking). Stand-in: a pattern grep of `git log -p --all` for private keys, cloud and GitHub tokens and quoted passwords found only test values (`dummypw1`, `hunter22`). `history-secrets-grep.txt` |
| Upstream advisories | Checked by the dependencies finder (boundary 11) |

## Pass log

| Pass | Date | Finders | Findings (C/H/M/L) | Rejected |
|---|---|---|---|---|
| Security | 2026-10-30 | 10 finders (boundaries 4 and 5 shared one), 8 verifiers | 1 / 3 / 15 / 15 | 9 |

Findings: `findings-security.md` (S-1 to S-34). Rejected: `rejected-security.md`. Finders and verifiers used scratch
programs outside the repo, mostly in the session scratchpad, to construct failing inputs. Nothing in the repo was
edited or built during the pass. Unverified finder and verifier notes are kept in
`%LOCALAPPDATA%\orrery-audit\2026-10\raw\` (outside the repo).

Packets: `packets/00-README.md` (19 packets, written 2026-10-30; 18 run the same day, packet 18 blocked). Worker agents:
`.claude/agents/packet-{opus-high,opus-medium,sonnet-medium,sonnet-low}.md`.

## Packet runs

The `packet-*` agents weren't loaded in the session that ran them (they were added mid-session), so each worker was a
general-purpose agent given the same instructions, model, effort and worktree isolation.

Until packet 13, `dotnet build Orrery.sln` on `main` showed 5 CA1416 warnings in `tests/Orrery.Protocols.Ssh.Tests`
(`FakePageant.cs`, `SshAgentTests.cs`). They predated the packets; the baseline's "0 warnings" came from an incremental
build. Packet 13 fixed them.

| Packet | Commit | Findings closed | Tests after |
|---|---|---|---|
| 07 | dae4adb | S-10 (emulator), S-11, S-12, S-13, S-20, S-21 | 260 |
| 08 | 98428ab | S-14, S-10 (Telnet) | 262 |
| 17 | a63bbe5 | S-34 | 262 |
| 01 | f521420 | S-1, S-6, S-7 (our part) | 272 |
| 06 | 22b8fff | S-5, S-24, S-22 | 280 |
| 19 | 106b0b3 | owner request (lock files, source mapping) | 280 |
| 09 | 3556946 | S-15, S-16, backstop | 281 |
| 03 | 85e5762 | S-3 | 292 |
| 15 | 9a146ff | S-29, NLA and fail-closed notes | 309 |
| 16 | b6adf83 | S-30 | 313 |
| 04 | e5fb86c | S-8, S-4 (temp file) | 318 (+6 Unix-only, skipped on Windows: not run) |
| 02 | a6d36a2 | S-2 | 325 (+6 skipped) |
| 05 | ced2fb5 | S-4 | 335 (+6 skipped) |
| 13 | 6067c61 | S-25, S-26, S-27, S-28 | 340 (+6 skipped); build now 0 warnings |
| 14 | 73eef43 | S-23 | 353 (+6 skipped) |
| 10 | a6a7711 | S-17, S-18, S-19, S-33 | 374 (+6 skipped) |
| 11 | fbcf720 | S-31, S-32 | 399 (+6 skipped) |
| 12 | 1f0d711 | S-9 | 401 (+6 skipped) |
| 15 fix | (this commit) | packet 15 refused HYBRID_EX's 4-byte Early User Authorization Result ("sign-in reply wasn't valid"); the reply size comes from IronRDP's hint again, capped at 64 KiB | 406 (+6 skipped) |

Final run on `main` at 1f0d711: `dotnet build Orrery.sln --no-incremental` 0 warnings, 0 errors; `dotnet test Orrery.sln`
401 passed, 6 skipped, 0 failed (Core 272 + 6 skipped, App 77, Ssh 27, Vnc 18, Telnet 7). The 6 skipped are Unix-only
file-mode tests (packet 04), not run on Windows. Every finding except S-7's IronRDP part (packet 18) is closed.

Not run, owner to check:
- a real SSH server with `yes | head -c 2G` leaves memory flat (06), and a legacy-only SSH server needs the new switch (14);
- a real RDP session connects, resizes and shares the clipboard, and a non-NLA server is refused (01, 09, 15);
- a real VNC server still connects with classic auth (01, 02);
- Move data and "Delete old copy" between two real folders, and the open-folder warnings on screen (03, 05);
- Linux or macOS file modes on a fresh data folder, and the 6 skipped tests (04);
- `build/Build-Installer.ps1` and the osx/arm64 publishes with locked restore (19).

Follow-ups noted by workers, not packeted:
- PuTTY 0.75+ offers a per-user named pipe; switching Pageant to it would avoid shared memory entirely (13).
- 8.3 short names aren't expanded in Move data's containment check; moving back into the default installed folder is
  refused because it isn't empty (03, both predate the round).
- `TerminalKeys.Paste` doesn't strip C1 characters (07).
- RDP `BuildConfig` doesn't cap the initial desktop size it requests at 8192 (01).
- VNC errors for an unsupported login method come through in the library's wording (02).

## Waiting on the owner

Nothing.

## Owner decisions

- 2026-10-30: profile confirmed as written, including its accepted risks.
- 2026-10-30: packet 02 (S-2), option A. The VNC client allows only None and classic VNC auth.
- 2026-10-30: packet 05 (S-4), option A. Protected ACLs on folders Orrery creates, plus a warning for folders open to
  other users.
- 2026-10-30: packet 12 (S-9), option A. A master password change also replaces the vault's data key.
- 2026-10-30: lower-integrity processes of the same user (a sandboxed browser renderer) count as crossing a
  boundary. Added to the profile; S-28 raised from Low to High (counts now 1 Critical, 4 High, 15 Medium, 14 Low).
- 2026-10-30: lock files in locked mode and a repo `nuget.config` with source mapping, yes. Packet 19.
