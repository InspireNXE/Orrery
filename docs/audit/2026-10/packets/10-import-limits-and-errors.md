# Packet 10: Make import fail cleanly on any bad file, cap how deep and how large it can be, and save its credentials off the UI thread

Rating: Guided. Findings: S-17, S-18, S-19, S-33. Depends on: 04 (both edit `ConnectionStore.cs`).
Runner: `packet-opus-medium` (Opus, medium effort). Several files across Core and App, plus a nesting limit that also
affects folders made by hand.

## Rules
- Work in your worktree branch, based on `main` after packet 04 has merged. Stage only files you changed, by explicit path; one commit.
- Read `docs/design-storage-vault-import.md` §4 (import), especially §4.2, §4.4 and §4.9.
- `MRemoteNgImporterTests` and `MRemoteNgCryptoTests` are on the never-weaken list: add, don't loosen.
- Gate: `dotnet build Orrery.sln` with 0 errors and 0 warnings; `dotnet test tests/Orrery.Core.Tests --filter "FullyQualifiedName~Import|FullyQualifiedName~ConnectionStore"`
  while working; `dotnet test Orrery.sln` green at the end. New regression tests must be shown failing before the fix.
- Final report: files changed, tests added, build and test summary, Verify evidence, other notes.

## Why
The user imports a `confCons.xml` that could have come from anyone. The verifier ran each of these through the real importer.

- **S-17.** `MNode`'s constructor (`src/Orrery.Core/Import/MRemoteNgImporter.cs:206-213`) builds
  `element.Attributes().ToDictionary(a => a.Name.LocalName, ...)`. Namespace declarations count as attributes, so
  `<Node Name="n" xmlns:Name="urn:x" .../>` throws `ArgumentException`. With `FullFileEncryption="true"` and
  `Protected=""` the importer uses mRemoteNG's public default key, and the decrypted body is loaded at `:172-179`,
  outside the only `XmlException` guard (`:139-148`). `ImportAsync` (`src/Orrery.App/MainWindow.axaml.cs:1504-1529`)
  catches only `ImportException`, IO and UnauthorizedAccess exceptions (and `MasterPasswordRequiredException`), and every
  caller is `async void`, so the app exits.
- **S-18.** `ConnectionStore`'s `JsonOptions` (`src/Orrery.Core/Connections/ConnectionStore.cs:17-23`) leaves `MaxDepth`
  at the default 64, and each folder level costs two JSON levels. 29 nested containers in an imported file, or 31
  nested folders made by hand with New folder (`MainWindow.axaml.cs:725-747`), make `Save` throw `JsonException`.
  `SaveNow` (`:277-288`) doesn't catch it, so the app exits from the import or the save timer (`:183`). Every depth that
  saves also loads. Separately, about 1,400 nested containers overflow the stack in `Builder.Add` (`:297-300`), which
  can't be caught.
- **S-19.** The only limit is `MaxCharactersInDocument` (64M, `:136`). `ImportPreviewWindow`
  (`src/Orrery.App/Import/ImportPreviewWindow.cs:42-45, 87-102`) adds one wrapping `TextBlock` per skipped or warning
  line to a non-virtualized `StackPanel`: 1.1 million lines blocked the UI about 66 s and used 10.8 GB. The credential
  loop (`MainWindow.axaml.cs:1545-1558`) awaits `SaveAsync` per credential; both stores complete synchronously, so it runs
  on the UI thread and rewrites the whole index or vault each time (3,000 took 27.5 s). Killing the app partway leaves
  credentials saved and connections not added.
- **S-33.** `KdfIterations` (`:169`) accepts any positive int, and `MRemoteNgCrypto.TryDecrypt` (`:35-78`) runs PBKDF2
  before checking the tag, with no caching, so a file with `int.MaxValue` and junk values costs about 24 minutes per value.
  `CredentialName` (`:372-382`) rescans every credential for each suffix it tries (cubic).

## Changes
1. S-17: make `MRemoteNgImporter.ImportFile` throw only `ImportException` (or `MasterPasswordRequiredException`) for bad
   input. Guard the inner load at `:172-179` the same way as the outer one. Read attributes with
   `element.Attribute("Name")` (empty namespace only) and ignore namespaced duplicates, or build the dictionary with
   last-wins on empty-namespace attributes only. In `ImportAsync`, add a final catch-all that traces the exception and
   shows "Couldn't import this file" with a generic message, so a future parser bug can't crash the app.
2. S-18:
   - Set `MaxDepth` in `ConnectionStore.JsonOptions` with headroom (e.g. 256), for both read and write.
   - Add a folder nesting limit (e.g. 60 levels) as a constant in `Orrery.Core.Connections`. Enforce it in New folder, in
     moving or dropping a folder into another (find every place `_tree.Add`/`Move` takes a folder), and in the importer.
     The UI says why when it refuses.
   - In the importer, check depth iteratively (walk the XML with an explicit stack, or check `XElement` depth before
     `MNode.Parse`) and throw `ImportException` above the limit, before any recursion.
   - `SaveNow`: also catch `JsonException` (and `NotSupportedException`) and show the same "Couldn't save your
     connections" message.
3. S-19:
   - Cap the counts in the importer: nodes (e.g. 20,000), report lines (keep the first 500, then a single "and N more"
     line), and credentials (e.g. 5,000), throwing `ImportException` above the node or credential cap.
   - Save credentials as one batch off the UI thread: add a `SaveManyAsync` (or similar) to the credential store
     interface that the vault and the keychain index implement with one rewrite of their file, run it in `Task.Run`, and
     show the existing busy state if one exists. Check both store implementations (`VaultFileStore`,
     `KeychainCredentialStore`) and any test fakes of the interface.
   - Only add the connections after credentials succeed, as now. If the batch fails partway, remove what it added, or
     say clearly in the message which were saved.
4. S-33: reject `KdfIterations` above 1,000,000 with `ImportException` (mRemoteNG's UI allows up to 50,000; check
   `MRemoteNgCrypto` and the design doc for the range before choosing). Keep a name-to-next-suffix map in
   `CredentialName`.
5. Tests in `tests/Orrery.Core.Tests/Import/MRemoteNgImporterTests.cs` and `.../Connections/`: one per crafted input
   (namespaced duplicate attribute, encrypted body with a DOCTYPE, encrypted body with malformed XML, 61 nested
   containers, 2,000 nested containers, 21,000 nodes, `KdfIterations="2147483647"`), each asserting `ImportException`
   and nothing else; `ConnectionStore` round-trips a 60-deep tree; the preview of a result with 600 skipped lines shows
   501 rows (headless App test).

Write your plan (files, the store interface change, the limits you chose and why, order of changes) and stop for review
before editing.

## Verify
- Each new test fails on `main` and passes after.
- `dotnet test Orrery.sln` green.

## Do not touch
- The XML reader settings (`DtdProcessing.Prohibit`, `XmlResolver = null`): they're correct.
- Move data in `MainWindow.axaml.cs` (packet 03) and the auto-lock handler (packet 11).
