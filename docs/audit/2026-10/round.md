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

Packets: `packets/00-README.md` (19 packets, written 2026-10-30, none run yet). Worker agents:
`.claude/agents/packet-{opus-high,opus-medium,sonnet-medium,sonnet-low}.md`.

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
