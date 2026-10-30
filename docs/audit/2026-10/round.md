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

Packets: `packets/00-README.md` (18 packets, written 2026-10-30, none run yet). Worker agents:
`.claude/agents/packet-{opus-high,opus-medium,sonnet-medium,sonnet-low}.md`.

## Waiting on the owner

1. **Lower-integrity processes.** Should a low-integrity process of the same user, such as a sandboxed browser
   renderer, count as crossing a boundary? The profile lists "another local user" but not this. The answer sets the
   severity of S-28 (Low or High) and part of S-25 and S-27.
2. **VNC security types (S-2).** The recommended fix allows only None and classic VNC auth, as upstream does. That
   drops VeNCrypt, TLS and RealVNC RA2 support until they can be wired to the certificate prompt. The alternative is
   keeping them and validating certificates and keys now, which is more work and still leaves the clear-text sub-types to block.
3. **Master password change (S-9).** A fresh data key on every change means editing design doc §3.3. Agree?
4. **Windows folder access (S-4).** Should Orrery set a protected ACL on folders it creates, warn on folders the
   user picks, or both?

## Owner decisions

- 2026-10-30: profile confirmed as written, including its accepted risks.
- 2026-10-30: packet 02 (S-2), option A. The VNC client allows only None and classic VNC auth.
