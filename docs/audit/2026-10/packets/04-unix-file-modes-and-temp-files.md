# Packet 04: Create data files 0600 and folders 0700 on Linux and macOS, and give atomic writes an unpredictable temp file

Rating: Mechanical. Findings: S-8, and the temp-file part of S-4. Depends on: 03 (both edit `App.axaml.cs` start-up code).
Runner: `packet-sonnet-medium` (Sonnet, medium effort).

## Rules
- Work in your worktree branch, based on `main` after packet 03 has merged. Stage only files you changed, by explicit path; one commit.
- Read `docs/design-storage-vault-import.md` §2.6 (file layout).
- Gate: `dotnet build Orrery.sln` with 0 errors and 0 warnings; `dotnet test tests/Orrery.Core.Tests --filter "FullyQualifiedName~Storage"`
  while working; `dotnet test Orrery.sln` green at the end. New regression tests must be shown failing before the fix
  (Unix-mode tests can only fail or pass on Unix; on Windows they're skipped, so say so).
- Final report: files changed, tests added, build and test summary, Verify evidence, other notes.

## Why
Every data file goes through `AtomicFile.WriteAllText` (`src/Orrery.Core/Storage/AtomicFile.cs:9-22`) or `File.Copy`.
Neither sets a Unix mode, and nothing in `src` uses `UnixCreateMode` or `UnixFileMode`, so .NET creates files
0666 and directories 0777 before the umask: 0644 and 0755 in practice. On a shared Linux machine with a 0755 home (older
Debian and Ubuntu, many servers), or for any portable or custom folder, another user can read `vault.frv` (offline
guessing), `credentials.json` (names, usernames, notes), `connections.json` and the trust stores. Linux uses the vault
exactly when there's no Secret Service, the headless and shared-server case. `File.Move(overwrite: true)` is
`rename(2)`, so a user's own `chmod 600` is lost on the next save.

The temp file is always `path + ".tmp"`, opened with `FileMode.Create`, which follows links and truncates. Once a data
folder is writable by someone else (packet 05), they can pre-create it as a link or hold it open to block saves.

## Changes
1. `AtomicFile.WriteAllText`:
   - create the directory with `Directory.CreateDirectory(path, UnixFileMode.UserRead | UserWrite | UserExecute)` on
     non-Windows (the overload throws on Windows; guard with `OperatingSystem.IsWindows()`);
   - write to a temp file in the same folder with a random name (e.g. `Path.GetFileName(path) + "." + random + ".tmp"`),
     opened with `FileMode.CreateNew` and, on non-Windows, `FileStreamOptions.UnixCreateMode = UserRead | UserWrite`;
   - on failure, delete the temp file before rethrowing;
   - keep the flush-to-disk and the final `File.Move(temp, path, overwrite: true)`.
   Check every caller still works (search `AtomicFile.WriteAllText`), and that nothing else looks for `*.tmp` files.
2. Same modes for the other writers into the data folder: `DataFolderLock.TryAcquire` (`DataFolderLock.cs:14-27`, the
   folder and `.lock`), `ConnectionStore`'s backup copy and backup folder (`src/Orrery.Core/Connections/ConnectionStore.cs:53-75`;
   after `File.Copy`, set the mode with `File.SetUnixFileMode`), and any `Directory.CreateDirectory` for the data, backup
   or import folders. Search `src/Orrery.Core` and `src/Orrery.App` for `Directory.CreateDirectory` and `File.Copy`.
3. At start-up, tighten an existing data folder once: on non-Windows, if the data folder's mode allows group or other
   access, set it to 0700. Put this next to `DataFolderLock.TryAcquire` or wherever the data folder is first opened in
   `App.axaml.cs`. Don't recurse into files; new saves fix their own modes.
4. Tests in `tests/Orrery.Core.Tests/Storage/` (new `AtomicFileTests.cs` if none exists):
   - Unix only (`[Fact(Skip=...)]` or an OS check that returns early with a clear message): a written file is 0600 and a
     created folder 0700; a file the user had chmod-ed stays 0600 after a rewrite.
   - All OSes: a pre-existing `<name>.tmp` (a plain file, and on Windows one held open with `FileShare.None`) doesn't
     stop a save and isn't overwritten or followed; no temp file is left behind after a save or after a failed one.

## Verify
- Tests pass on Windows; Unix-only tests are reported as skipped here, with the reason.
- `dotnet test Orrery.sln` green.

## Do not touch
- Windows ACLs (packet 05).
- `DataMover` (packet 03).
