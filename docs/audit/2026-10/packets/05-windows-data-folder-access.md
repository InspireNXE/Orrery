# Packet 05: Keep other Windows users out of a data folder outside the user profile

Rating: Decision. Findings: S-4. Depends on: 04 (both edit `AtomicFile` and `DataFolderLock`).
Runner: `packet-opus-high` (Opus, high effort). The trust stores live here; an open folder lets another
user plant a pin.

**Answer (2026-10-30):** A, both. Protected ACLs on folders Orrery creates, and a warning for folders the user picks
or that turn out to be open to others.

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

## Changes (option A, approved)
1. Add `src/Orrery.Core/Storage/FolderAccess.cs` (Windows only; on other systems both methods do nothing and report
   "not open", since packet 04 covers Unix modes):
   - `CreateProtected(string path)`: create the folder if missing and set a protected DACL (inheritance off) granting
     full control to the current user, SYSTEM and BUILTIN\Administrators only, with object and container inherit so
     files and sub-folders written later get it. If the folder already exists, leave its ACL alone.
   - `IsOpenToOthers(string path)`: true when any allow ACE grants write, modify, delete or full control to a SID other
     than the current user, SYSTEM, Administrators or CREATOR OWNER, or when the volume has no ACL support (FAT, exFAT;
     check `DriveInfo.DriveFormat` or a failed `GetAccessControl`). Inherit-only ACEs that don't reach the folder's
     files don't count.
2. Use `CreateProtected` wherever Orrery creates the data folder or one of its sub-folders: `DataFolderLock.cs:16`,
   `DataLocationResolver.cs:81`, `AtomicFile.cs:11`, `ConnectionStore.cs:58` (backups) and `DataMover.cs:53`. Under
   `%APPDATA%` this changes nothing visible.
3. Warn before using a folder that is open to others:
   - `DataMover.CheckTarget` (or a new sibling the Settings page calls after it) reports an open target as a warning,
     not an error. `SettingsView` (`:151-177`) shows "Other people who use this computer can change files in this
     folder. Orrery's saved hosts and trusted keys would live here." with Use anyway and Cancel.
   - Portable setup's custom folder and "with the app" (`PortableSetupWindow.cs:41, 73, 128-129`) show the same warning.
     For "with the app", add that others could also replace Orrery itself.
   - At start-up, if the data folder is open to others, say so once in the status bar. Don't block start-up.
4. Don't change the ACL of a folder the user picked that already existed. Only folders Orrery itself creates get one.
5. Tests in `tests/Orrery.Core.Tests/Storage/FolderAccessTests.cs` (skipped on non-Windows):
   - `CreateProtected` on a new folder: inheritance is off and only the three allowed SIDs have ACEs; a file written in
     it with `AtomicFile` gives no access to `Users` or `Authenticated Users`.
   - A folder under the test temp directory with an added `BUILTIN\Users` modify ACE: `IsOpenToOthers` is true; with
     only the owner's ACEs it is false.
   - `CreateProtected` on an existing folder leaves its ACL as it was.
   - `CheckTarget` (or its sibling) returns the warning for the open folder and nothing for a protected one.

## Verify
- The tests above pass and fail on `main`.
- `dotnet test Orrery.sln` green.
- Manual, in the final report: Move data to a new folder under `C:\` and run `icacls` on it and on `connections.json`
  inside it; only the current user, SYSTEM and Administrators should be listed. Picking an existing folder under `W:\`
  shows the warning.

## Do not touch
- Unix modes and the temp file (packet 04), the move and delete logic (packet 03).
