# Packet 03: Make Move data copy and delete only Orrery's own files, and refuse folders inside each other however they're spelled

Rating: Guided. Findings: S-3. Depends on: nothing.
Runner: `packet-opus-high` (Opus, high effort). This code deletes files; a mistake loses user data.

## Rules
- Work in your worktree branch, based on `main`. Stage only files you changed, by explicit path; one commit.
- Read `docs/design-storage-vault-import.md` §2.2 (storage modes), §2.6 and §2.7 (Move data).
- `DataMoverTests` and `DataLocationResolverTests` are on the profile's never-weaken list: add to them, don't loosen
  or delete any existing test.
- Tests must only ever touch folders under the test's own temp directory. Never point `DataMover` at a real folder.
- Gate: `dotnet build Orrery.sln` with 0 errors and 0 warnings; `dotnet test tests/Orrery.Core.Tests --filter "FullyQualifiedName~DataMover"`
  while working; `dotnet test Orrery.sln` green at the end. New regression tests must be shown failing before the fix.
- Final report: files changed, tests added, build and test summary, Verify evidence, other notes.

## Why
`DataMover.DataFiles` (`src/Orrery.Core/Storage/DataMover.cs:99-104`) returns every file under the folder,
recursively, skipping only the top-level `.lock` and `data-location.json`. `Copy` (`:43-70`) copies all of them and
`DeleteOldCopy` (`:76-95`) deletes all of them, then every directory left empty. The doc comment at `:72-75` promises
"files the user added stay", and nothing implements that. The verifier reproduced each of these in scratch folders:

- A portable copy's custom folder can hold other files: `PortableSetupWindow` (`src/Orrery.App/Storage/PortableSetupWindow.cs:110-133`)
  only checks it's writable, and §2.2 says an existing folder is reused. A user's `Thesis.docx` and `Photos\` were
  copied, then deleted from the old folder.
- The installed default folder (`%APPDATA%\Orrery`, `DataLocation.cs:46`) contains `Portable\` when a portable copy
  chose "user profile" (`DataLocationResolver.cs:35`). Moving the installed copy and deleting the old one removed that
  portable copy's data.
- `Directory.EnumerateFiles(..., AllDirectories)` follows junctions, so the files of a junction's target outside the
  data folder were deleted.
- `IsInside` (`:136-137`) tests `StartsWith(folder + separator)`. A root such as `E:\` already ends in a separator,
  so a move from `E:\` to `E:\Backup` passes `CheckTarget`.
- `CheckTarget` compares `Path.GetFullPath` strings and resolves no links. With a target reached through a junction
  alias into the source, the copy recursed into itself and "Delete old copy" deleted the new data folder.

Separately, `--moved-from <path>` (`src/Orrery.App/App.axaml.cs:84-85`) feeds `OfferToDeleteOldCopyAsync`
(`src/Orrery.App/MainWindow.axaml.cs:1669-1686`), which checks only `Directory.Exists` and `!SamePath`.

## Changes
1. Define the set of things Orrery owns in a data folder, in one place in `Orrery.Core.Storage` (reuse the existing
   `FileName` constants where they exist): `connections.json`, `settings.json`, `recent.json`, `vault.frv`,
   `credentials.json`, `known-hosts.json`, `trusted-certificates.json`, the `backups` folder (only
   `connections-*.json` inside it) and the `import` folder (only `mremoteng-*.md` inside it). Search `src/` for every
   file the app writes into the data folder (`location.FileIn(`, `AtomicFile.WriteAllText`) and make sure the list is
   complete; report anything you add.
2. `Copy` and `DeleteOldCopy` work only from that list. Never recurse into `Portable\` or anything not on the list,
   never follow a reparse point (check `FileAttributes.ReparsePoint` on each directory and file you'd touch, and skip
   it), and delete a listed folder only if it's empty afterwards. The old folder itself is removed only if it ends up
   empty. Leftover user files are reported back (return a count or list) so the UI can say "N files that weren't
   Orrery's were left in place."
3. Replace the string containment check with one that works for roots and links: normalise both paths, resolve every
   existing ancestor through `FileSystemInfo.ResolveLinkTarget(returnFinalTarget: true)` or
   `Path.GetFullPath` on the resolved target, and compare segment by segment. A root is a valid "folder" for the check.
   Refuse a move whose source or target is a drive or filesystem root outright.
4. `--moved-from`: when Move data succeeds, record the old path in the new data folder (a small file such as
   `pending-cleanup.json`, written with `AtomicFile`). On restart, offer to delete only if that file exists and names
   the same path as `--moved-from`, then remove the file. Refuse a path that contains or is inside the current folder.
   Check how `SettingsView.cs:149-193` and `App.axaml.cs:92-121` hand the path over before choosing the file's shape.
5. Update the `DeleteOldCopy` doc comment and the Move data confirmation text if the behaviour it describes changes.
   Update design doc §2.7 to say what Move data copies and deletes.
6. Tests in `tests/Orrery.Core.Tests/Storage/DataMoverTests.cs`, one per case:
   - user files (a `.docx` at the top, a `Photos\` folder) survive both copy and delete, and aren't copied;
   - a nested `Portable\` folder is neither copied nor deleted;
   - a junction (Windows; skip on other OSes) or a symlink (Unix) inside the data folder: its target's files survive;
   - `CheckTarget` refuses a root source, a target inside the source through a junction alias, and the source inside
     the target;
   - the existing tests still pass unchanged.

Write your plan (files, the shape of the owned-files list and the containment helper, the pending-cleanup file, order of
changes) and stop for review before editing.

## Verify
- Every new test fails on `main` and passes after.
- `dotnet test Orrery.sln` green.
- Report the exact text the user now sees after "Delete old copy" when files were left behind.

## Do not touch
- `AtomicFile`, `DataFolderLock` and permissions (packets 04 and 05).
- The import flow in `MainWindow.axaml.cs` (packet 10) and the vault auto-lock handler (packet 11).
