# Packets, round 2026-10

Work packets for the security pass. Each one closes findings from `../findings-security.md` and can be run by a session
that has read nothing else. Line numbers come from `58e9c72` (the findings commit, `deffa86`, changed only docs).
Nothing here has run yet.

| # | Packet | Rating | Findings | Depends on | Runner | State |
|---|---|---|---|---|---|---|
| 01 | [Cap remote desktop sizes](01-cap-remote-desktop-sizes.md) | Mechanical | S-1, S-6, S-7 (part) | — | opus-high | Ready |
| 02 | [Restrict VNC security types](02-restrict-vnc-security-types.md) | Decision | S-2 | 01 (file) | opus-high | Ready after 01 |
| 03 | [Move data touches only Orrery's files](03-move-data-only-orrery-files.md) | Guided | S-3 | — | opus-high | Ready |
| 04 | [Unix file modes and safe temp files](04-unix-file-modes-and-temp-files.md) | Mechanical | S-8, S-4 (part) | 03 (file) | sonnet-medium | Ready |
| 05 | [Windows data folder access](05-windows-data-folder-access.md) | Decision | S-4 | 04 | opus-high | Ready after 04 |
| 06 | [SSH stream, writes and failure text](06-ssh-stream-and-writes.md) | Guided | S-5, S-24, S-22 | — | opus-medium | Ready |
| 07 | [Terminal emulator limits](07-terminal-emulator-limits.md) | Mechanical | S-10, S-11, S-12, S-13, S-20, S-21 | — | sonnet-medium | Ready |
| 08 | [Telnet limits and faults](08-telnet-limits-and-faults.md) | Mechanical | S-14, S-10 (part) | — | sonnet-low | Ready |
| 09 | [Contain session crashes](09-contain-session-crashes.md) | Mechanical | S-15, S-16, backstop | 01 | sonnet-medium | Ready after 01 |
| 10 | [Import limits and errors](10-import-limits-and-errors.md) | Guided | S-17, S-18, S-19, S-33 | 04 (file) | opus-medium | Ready after 04 |
| 11 | [Vault header and auto-lock](11-vault-header-and-auto-lock.md) | Mechanical | S-31, S-32 | 10 (file) | sonnet-medium | Ready after 10 |
| 12 | [New data key on password change](12-new-data-key-on-password-change.md) | Decision | S-9 | 11 | opus-high | Ready after 11 |
| 13 | [SSH agent hardening](13-ssh-agent-hardening.md) | Guided | S-25, S-26, S-27, S-28 | 06 | opus-high | Ready after 06 |
| 14 | [SSH legacy algorithms](14-ssh-legacy-algorithms.md) | Mechanical | S-23 | 13 (file) | sonnet-medium | Ready after 13 |
| 15 | [RDP connect hardening](15-rdp-connect-hardening.md) | Mechanical | S-29, notes | 09 (file) | sonnet-medium | Ready after 09 |
| 16 | [Focus on connect](16-focus-on-connect.md) | Mechanical | S-30 | 06, 15 (file) | sonnet-low | Ready after 06 and 15 |
| 17 | [BouncyCastle pin](17-bouncycastle-pin.md) | Mechanical | S-34 | — | sonnet-low | Ready |
| 18 | [IronRDP upgrade](18-ironrdp-upgrade.md) | Mechanical | S-7 | 01 | sonnet-low | Blocked: no package with the fixes yet |

"(file)" means the packets don't need each other's results but edit the same files, so they run one after the other.

All 34 findings are covered: S-1 to S-34 each appear in exactly one packet's Findings, except S-4, S-7 and S-10, which
two packets split (noted as "part").

## Runner rule

Runners are the agents in `.claude/agents/packet-*.md`, chosen by how much judgement the packet needs:
- **opus-high:** a subtle mistake reopens a hole or loses data. That's the memory-corruption fix (01), anything that
  sends or stores credentials (02, 12, 13), deleting files (03), and the trust stores' folder access (05).
- **opus-medium:** Guided packets that need judgement across several files: threading in the SSH session (06), and import
  across Core, App and the credential store interface (10).
- **sonnet-medium:** Mechanical packets that touch several files or need new tests (04, 07, 09, 11, 14, 15).
- **sonnet-low:** Mechanical packets that change a few lines in one place (08, 16, 17, 18).

## Owner decisions

Open:
1. **Scope of lower-integrity processes.** Should a low-integrity process of the same user (a browser sandbox) count as
   crossing a boundary? It sets S-28's severity (Low or High). Packet 13 is worth running either way.
2. **Lock files.** Packet 17 only fixes the BouncyCastle pin. Should restores use `packages.lock.json` with locked mode,
   plus a repo `nuget.config` with source mapping? Not packeted yet.

Decided:
- 2026-10-30: profile confirmed as written, including its accepted risks.
- 2026-10-30: packet 02, option A. Only None and classic VNC auth.
- 2026-10-30: packet 05, option A. Protected ACLs on folders Orrery creates, plus a warning for open folders.
- 2026-10-30: packet 12, option A. New data key on every master password change.

## Orchestrator

"Run these packets" or `/audit run all` makes the session that receives it the orchestrator. It follows this section
exactly.

### Ground rules
- Base branch is `main`. There's no remote: never push, never create one.
- Owner's standing rule: commit as you go on `main`, one commit per packet. Workers commit once on their own worktree
  branch; the orchestrator brings that commit onto `main`.
- Run only packets whose State is Ready (or "Ready after" once those packets are Done). Skip Decision packets until
  their answer is recorded at the top of the packet file and in "Owner decisions" above. Skip Blocked packets.
- Workers are the agents named on each packet's Runner line, started with the Agent tool by that `subagent_type` and
  the prompt "Carry out audit packet NN: docs/audit/2026-10/packets/<file>. Line numbers are from 58e9c72." Never
  override their model.
- The owner may have their own Orrery open from `W:\fluentRemote`. Workers build in their worktrees and don't need it
  closed. Before building in `W:\fluentRemote` itself, stop only Orrery processes this session started; if a build is
  locked by one you didn't start, ask the owner first.

### Order and concurrency
Run these four chains side by side, at most four workers at once. Within a chain, one packet at a time, in order:
- A, remote frames: 01, 09, 15, then 02 when answered (it edits `VncSession.cs` after 01).
- B, storage and the main window: 03, 04, 10, 11, then 05 and 12 when answered.
- C, SSH: 06, 13, 14.
- D, independent: 07, 08, 17.
Packet 16 goes last, after 06 and 15 are both Done. Packet 18 waits for its upstream package.

Two packets that edit the same file never run at once. If a worker's plan or report shows it touching a file another
running packet owns, stop the later one and requeue it after the earlier one is merged.

### How a packet is run
1. Start the worker (above). Mark the packet "Running" in the table.
2. **Guided packets** return a plan first. Check it against the packet's Changes and "Do not touch". If it stays inside
   them, reply to the same agent (SendMessage) with "Plan approved, go ahead." If it changes scope, a file format,
   user-visible behaviour the packet didn't describe, or drops a test, stop and ask the owner, quoting the plan.
3. When the worker reports, read its diff (`git -C <worktree> show`) and check: the packet's Verify steps have evidence;
   each new test was shown failing first (or the report says why it couldn't be); nothing in "Do not touch" changed; no
   never-weaken test (see `.claude/audit-profile.md`) was loosened or deleted. Send problems back to the same agent.
4. Bring the commit onto `main` with `git cherry-pick <sha>` from `W:\fluentRemote`. On a conflict, resolve it only if
   it's mechanical (both sides kept); otherwise abort the cherry-pick and send the packet back to a new worker based on
   the current `main`.
5. On `main`, run `dotnet build Orrery.sln` and `dotnet test Orrery.sln`. A red suite after a cherry-pick means
   `git revert` that commit and send the packet back with the failure. Never leave `main` red.
6. Remove the worker's worktree and branch (`git worktree remove`, `git branch -D`) once its commit is on `main`.
7. Mark the packet "Done (<short sha>)" in the table, and add a line to `../round.md`'s pass log: packet, commit,
   findings closed, test count.
8. A gate that couldn't run (a Unix-only test on Windows, a live check) is recorded as not run, never as passed.

### Finishing
1. When every runnable packet is Done, run `dotnet test Orrery.sln` on `main` once more and record the counts in `../round.md`.
2. Commit the README and round file updates (one commit, "Record packet results for audit 2026-10").
3. Report to the owner: packets done with their commits, packets skipped and why (waiting on a decision, blocked),
   tests added, and the live checks only the owner can do:
   - a real SSH server with `yes | head -c 2G` leaves Orrery's memory flat (06);
   - a real RDP session still connects, resizes and shares the clipboard (01, 09, 15);
   - a real VNC server still connects with classic auth (01, 02);
   - Move data and "Delete old copy" between two real folders (03);
   - Linux or macOS file modes on a fresh data folder (04).
4. Suggest the tag `audit-2026-10` (on the commit before the first packet, `deffa86`) if the owner hasn't made it, and
   ask before creating it.
