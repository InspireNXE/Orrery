# Audit round 2026-11

Security pass over what landed after round 2026-10, plus a correctness pass over the newest UI work, run 2026-11-03
on `dev` at `a80e35d` (round 2026-10 ended at `a675c27`). Profile: `.claude/audit-profile.md`.

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
| Bugs (after the fixes above) | 5 (RDP, recording, main window, Core and import, other protocols and the terminal) + 1 over the fixes | – | 32 | 32 | 0 |

Findings: `findings-security.md`; the bug pass, `findings-bugs.md`.

## After the fixes

`dotnet build Orrery.sln`: 0 errors. `dotnet test Orrery.sln`: all passed (Core 938 and 6 skipped, App 496, RDP 76,
SSH 105, VNC 25, Serial 10, Local 12, Telnet 11).

Not checked on a real machine: the web fixes (S-1, S-10, S-11 need a live WebView2), the vault lock closing the
player (S-7), the pane widths on a small screen (B-1), and the Unix clipboard folder (S-16, Windows here).

## Built after the round

The owner asked for all four items that were waiting on them ("Build all four"):

- S-4: a jump host's forwarded port takes one connection, and refuses and logs the rest.
- S-13: deleting Move data's old copy removes the web folder, and Settings > Web pages can clear web data.
- S-20: host checks are a per-folder setting, and an import asks before checking the hosts it adds.
- S-22: mRemoteNG exports with passwords use 50,000 PBKDF2 rounds.

`dotnet build Orrery.sln`: 0 errors. `dotnet test Orrery.sln`: all passed (Core 943 and 6 skipped, App 496, RDP 76,
SSH 106, VNC 25, Serial 10, Local 12, Telnet 11). Not checked on a real machine: clearing web data while WebView2 holds
it, and the import prompt.

## Bug pass

See `findings-bugs.md`. `dotnet build Orrery.sln`: 0 errors. `dotnet test Orrery.sln`: all passed (Core 960 and 6
skipped, App 508, RDP 84, SSH 107, VNC 25, Serial 10, Local 12, Telnet 12).
