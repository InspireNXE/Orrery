# Audit round 2026-11

Security pass over what landed after round 2026-10, plus a correctness pass over the newest UI work, run 2026-11-03
on `dev` at `4ae708e` (round 2026-10 ended at `bfdd177`). Profile: `.claude/audit-profile.md`.

Run lighter than 2026-10 at the owner's request ("if it's more economical, do it during the security audit"): six
finders and no separate verifier agents. Fixes were made in the same session, where they keep every feature, as the
owner asked ("fix any security issues found that aren't compromising to the feature set"). No packets.

## Baseline

| Check | Result |
|---|---|
| Working tree | Clean, `dev` even with `origin/dev` |
| `dotnet list package --vulnerable --include-transitive` | No known vulnerable packages |
| `dotnet list package --deprecated` | Only `xunit` 2.9.3 (test-only) |
| Upstream advisories | SSH.NET's four 2026 advisories are fixed in 2026.0.0, which Orrery uses; none for Avalonia, Avalonia.Controls.WebView or IronRDP |

## Pass log

| Pass | Finders | Findings (M/L) | Bugs | Fixed | Open |
|---|---|---|---|---|---|
| Security + correctness | 6 (web, RDP channels and the fork's drive backend, recording, import and secrets, supply chain, UI) | 9 / 20 | 7 | 22 | 14 |

Findings: `findings-security.md`.

## After the fixes

`dotnet build Orrery.sln`: 0 errors. `dotnet test Orrery.sln`: all passed (Core 938 and 6 skipped, App 496, RDP 76,
SSH 105, VNC 25, Serial 10, Local 12, Telnet 11).

Not checked on a real machine: the web fixes (S-1, S-10, S-11 need a live WebView2), the vault lock closing the
player (S-7), the pane widths on a small screen (B-1), and the Unix clipboard folder (S-16, Windows here).

## Waiting on the owner

- S-4: jump-host and forward listeners on loopback accept other local users. Needs a design.
- S-13: Move data and the web profile, and a "clear web data" action.
- S-20: host checks probing hosts from an import before the user connects to one.
- S-22: the mRemoteNG export's PBKDF2 count.
