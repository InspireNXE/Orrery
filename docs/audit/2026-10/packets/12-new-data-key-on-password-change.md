# Packet 12: Give the vault a new data key when the master password changes

Rating: Decision. Findings: S-9. Depends on: 11 (both edit `VaultFileStore.cs`).
Runner: `packet-opus-high` (Opus, high effort). Vault key handling; a mistake loses every saved credential.

**Answer (2026-10-30):** A, a new data key on every master password change, with design doc §3.3 updated to match.

## Question
Changing the master password today re-wraps the same data key (design doc §3.3 says so on purpose). An old copy of
`vault.frv` plus the old password therefore still decrypts the current file, including credentials added after the
change. Should a password change also replace the data key?

- **A. Yes, new data key on every change (recommended).** `Save` already re-encrypts the whole payload during a change,
  so the cost is one extra random key. Old copies stay readable with the old password, which is unavoidable, but nothing
  saved after the change is. §3.3 gets updated. No other feature depends on the key staying the same ("remember on this
  device" in §3.4 isn't built).
- **B. Keep it as designed, and say so in the UI.** Change "Your saved credentials stay as they are"
  (`ChangeMasterPasswordWindow.cs:46`) to tell the user that old backups still open with the old password and that
  credentials should be rotated if the old password leaked. No code change to the vault.

## Rules
- Work in your worktree branch, based on `main` after packet 11 has merged. Stage only files you changed, by explicit
  path; one commit.
- Read `docs/design-storage-vault-import.md` §3.3 to §3.5.
- `VaultFileStoreTests` is on the never-weaken list (wrong password, tampering, master password change): add, don't loosen.
- Gate: `dotnet build Orrery.sln` with 0 errors and 0 warnings; `dotnet test tests/Orrery.Core.Tests --filter "FullyQualifiedName~Vault"`
  while working; `dotnet test Orrery.sln` green at the end. New regression tests must be shown failing before the fix.
- Final report: files changed, tests added, build and test summary, Verify evidence, other notes.

## Why
`ChangePasswordAsync` (`src/Orrery.Core/Credentials/VaultFileStore.cs:189-254`) unwraps the data key with the old
password (`:212-217`), wraps the same bytes under a key derived from the new one (`:226-230`), and calls `Save` (`:240`),
which encrypts the payload with the unchanged `_dataKey` (`:322-336`). The associated data comes only from the plaintext
header (`:381-382`). The verifier built the attack with the real class: it created a vault, copied the file, changed the
password, added a credential with secret `pw-ADDED-AFTER-CHANGE`, then unwrapped the key from the old copy with the old
password and decrypted the current payload, including that secret. The realistic case is a user who suspects the old
password leaked, changes it, then rotates server passwords, while an old copy sits in a synced folder's version history or
a backup.

## Changes (option A, approved)
1. In `ChangePasswordAsync`, after verifying the old password, generate a fresh data key (as vault creation does; find
   that code and reuse it), wrap the new key under the new password, and inside the lock swap `_dataKey` for the new key
   before `Save`. On failure, restore both `_file` and the old `_dataKey` exactly as the current rollback restores `_file`,
   and dispose whichever key is no longer used. Zero the raw key bytes in `finally` as now.
2. Update §3.3 of the design doc, and the comment at `:239`, to say the data key changes with the password.
3. Tests in `VaultFileStoreTests.cs`:
   - the attack above, done with the public API plus the file format: after a change, the data key unwrapped from a
     pre-change copy with the old password does not decrypt the post-change payload. If that needs the key bytes, use the
     file format directly in the test (it's documented in §3.3), not private members.
   - all existing change-password tests still pass; a change that fails partway (e.g. the file is read-only) leaves the
     vault unlockable with the old password and its credentials intact.

## Verify
- The new tests fail on `main` and pass after.
- `dotnet test Orrery.sln` green.

## Do not touch
- Header validation (packet 11).
