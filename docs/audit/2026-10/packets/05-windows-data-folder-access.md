# Packet 05: Keep other Windows users out of a data folder outside the user profile

Rating: Decision. Findings: S-4. Depends on: 04 (both edit `AtomicFile` and `DataFolderLock`).
Runner: `packet-opus-high` (Opus, high effort), once answered. The trust stores live here; an open folder lets another
user plant a pin.

**Answer:** not given yet.

## Question
A data folder created under `C:\`, on a second NTFS or ReFS drive, or on exFAT, can be changed by every user who logs
into the machine. What should Orrery do?

- **A. Both (recommended).** Give every folder Orrery creates a protected ACL (current user, SYSTEM, Administrators,
  inheritance off). When the user picks an existing folder, or a folder on a drive with no ACLs (FAT/exFAT), check
  whether anyone else can write to it and warn before using it, with the option to go ahead. Also re-check at start-up
  and say so once in the status bar if the folder is open to others.
- **B. Set ACLs only.** Silent and simple, but an existing folder the user picks, or an exFAT stick, stays open
  without the user knowing.
- **C. Warn only.** No ACL code, but the user has to fix permissions by hand.

## Rules
- Work in your worktree branch, based on `main` after packet 04 has merged. Stage only files you changed, by explicit
  path; one commit.
- Read `docs/design-storage-vault-import.md` §2.2 (storage modes) and §2.7 (Move data).
- Gate: `dotnet build Orrery.sln` with 0 errors and 0 warnings; focused tests while working; `dotnet test Orrery.sln`
  green at the end. New regression tests must be shown failing before the fix.
- Final report: files changed, tests added, build and test summary, Verify evidence, other notes.

## Why
No code sets or checks an ACL (no `SetAccessControl`, `FileSecurity` or `DirectorySecurity` anywhere), so data folders
inherit their parent's. On this machine `icacls C:\` shows `Authenticated Users:(OI)(CI)(IO)(M)`, and `E:\` and `W:\`
grant the same; `D:\` is exFAT with no ACLs at all. Folders get there through Move data
(`src/Orrery.App/Settings/SettingsView.cs:151-177`, `src/Orrery.Core/Storage/DataMover.cs:53-54`, where `File.Copy`
takes the target's inherited ACL) and portable setup's custom folder (`src/Orrery.App/Storage/PortableSetupWindow.cs:128-129`).
Another user can then rewrite a saved connection's host in `connections.json` and add their key to `known-hosts.json`
or `trusted-certificates.json`, which are plain JSON (`src/Orrery.Core/Security/JsonCertificateTrustStore.cs:40-53`).
The next connect sends the saved password to them with no prompt. `%APPDATA%\Orrery` is fine as it is. For portable
"with the app" (`PortableSetupWindow.cs:41, 73`), a folder others can write also lets them replace `Orrery.exe`, so only
the warning helps there.

## Changes
Empty until the owner answers. For option A the work is: a helper in `Orrery.Core.Storage` that creates a folder with a
protected DACL and one that reports whether any SID other than the current user, SYSTEM and Administrators has write,
modify or full control (and treats "no ACL support" as open); use the first wherever Orrery creates the data folder and
its sub-folders, and the second in `CheckTarget`, portable setup and start-up; tests that create folders under the test
temp directory with an extra `Users` write ACE and check the warning, and that a created folder ends up protected.

## Verify
To be written with the answer.

## Do not touch
- Unix modes and the temp file (packet 04), the move and delete logic (packet 03).
