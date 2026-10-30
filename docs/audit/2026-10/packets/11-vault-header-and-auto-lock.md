# Packet 11: Reject a damaged vault header cleanly, and clear the credential picker when the vault locks itself

Rating: Mechanical. Findings: S-31, S-32. Depends on: 10 (both edit `MainWindow.axaml.cs`).
Runner: `packet-sonnet-medium` (Sonnet, medium effort).

## Rules
- Work in your worktree branch, based on `main` after packet 10 has merged. Stage only files you changed, by explicit path; one commit.
- Read `docs/design-storage-vault-import.md` §3.3 (vault format).
- `VaultFileStoreTests` is on the never-weaken list: add, don't loosen. Tests use small KDF settings; follow the existing
  tests' set-up.
- Gate: `dotnet build Orrery.sln` with 0 errors and 0 warnings; `dotnet test tests/Orrery.Core.Tests --filter "FullyQualifiedName~Vault"`
  and the App tests while working; `dotnet test Orrery.sln` green at the end. New regression tests must be shown failing
  before the fix.
- Final report: files changed, tests added, build and test summary, Verify evidence, other notes.

## Why
- **S-31.** `VaultFileStore.Open` (`src/Orrery.Core/Credentials/VaultFileStore.cs:129-181`) checks only the format,
  version and algorithm strings, then dereferences `file.Kdf`, `file.WrappedKey` and `file.Payload`, decodes base64, and
  hands the KDF parameters to Argon2 (`DeriveKeyEncryptionKey`, `:360-379`) before the associated-data check. A missing
  block, bad JSON, bad base64 or out-of-range KDF parameters throw `NullReferenceException`, `JsonException`,
  `FormatException`, `ArgumentNullException` or `ArgumentException`. `VaultUnlockWindow` (`src/Orrery.App/Credentials/VaultUnlockWindow.cs:124-133`)
  and `ChangeMasterPasswordWindow` (`:95-104`) catch only `WrongVaultPasswordException`, `InvalidDataException`, IO and
  UnauthorizedAccess exceptions, from `async void` handlers, so the app exits. A headless run showed each one reaching the
  dispatcher's unhandled path. `iterations` near `int.MaxValue` keeps a core busy for months, even after Cancel. Anyone who
  can write the file can delete it anyway, so this is mostly about a sync conflict or a damaged copy not crashing the app.
- **S-32.** The `AutoLocked` handler (`src/Orrery.App/MainWindow.axaml.cs:165-172`) updates the vault button and the
  credentials side list, but not the inspector. The manual lock (`OnLockVaultClick`, `:1598-1605`) also calls
  `_properties.TrySave()` and `await _properties.ShowAsync(_properties.Node)`, which rebuilds `CredentialPicker`
  (`src/Orrery.App/Credentials/CredentialPicker.cs:37-75`). After an auto-lock the dropdown still lists every credential
  as "Name (DOMAIN\user)", which the vault file keeps encrypted. `CredentialsView.cs:194` states the intent: nothing from a
  locked store stays visible.

## Changes
1. S-31: add header validation at the top of `Open`, before any key derivation: required blocks present; salt and nonces
   decode from base64 to their expected lengths (salt `SaltSize`, nonces `Aead.NonceSize`); KDF memory, iterations and
   parallelism within bounds. Set the bounds around the defaults used for new vaults (find `_kdfForNewVaults`): for
   example memory 8 MiB to 1 GiB, iterations 1 to 100, parallelism exactly 1 (the only value written). Throw
   `InvalidDataException("The vault file is damaged.")` for any failure, and wrap the JSON read so `JsonException` becomes
   `InvalidDataException` too. Keep the existing "isn't an Orrery vault" message for the format and version check.
2. Check `ChangePasswordAsync` (`:189-254`): it decodes the in-memory header, which is already validated by `Open`, so it
   should need nothing more; say so in the report if that holds.
3. S-32: in the `AutoLocked` handler, do what the manual lock does after locking: `_properties.TrySave()` (check it's safe
   to call when the vault is already locked and the node references a vault credential), then
   `await _properties.ShowAsync(_properties.Node)`. The handler is a posted lambda; make it async and catch so it can't
   throw onto the dispatcher. Also look at `ConnectPromptWindow.cs:73-77`, which lists credentials too: if it's open when the
   vault auto-locks, close or refresh its list the same way, or report that it's left as is and why.
4. Tests:
   - `tests/Orrery.Core.Tests/Credentials/VaultFileStoreTests.cs`: each damaged header (truncated JSON, `kdf` missing,
     `wrappedKey` missing, salt bad base64, nonce wrong length, iterations 0, iterations 1,000,000, parallelism 2, memory 7)
     makes `UnlockAsync` throw `InvalidDataException`, quickly (no long derivation).
   - `tests/Orrery.App.Tests`: with a vault on a short auto-lock timer and a connection whose inspector shows the picker,
     after auto-lock the picker no longer lists any credential name (mirror how `PropertiesPanelTests.cs` builds the panel).

## Verify
- Each new test fails on `main` and passes after.
- `dotnet test Orrery.sln` green.

## Do not touch
- Key handling in `ChangePasswordAsync` (packet 12).
- The import flow and Move data code in `MainWindow.axaml.cs` (packets 10 and 03).
