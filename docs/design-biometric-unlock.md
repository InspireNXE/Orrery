# Orrery design: unlocking the vault with Windows Hello or Touch ID

Status: **Built**, 2026-10-31 (revised after the security review the same day). Windows Hello and Touch ID still need the manual checks in §6.3
Scope: opening `vault.frv` with Windows Hello or Touch ID instead of typing the master password. Builds on §3 of
[design-storage-vault-import.md](design-storage-vault-import.md).

---

## 1. Goals and non-goals

**Goals**
- Open the vault with a face, fingerprint or Windows Hello PIN on Windows, and Touch ID on macOS.
- A stolen data folder is no easier to open than it is with the master password alone.
- The master password always still works. Turning this on never changes the vault file.
- Changing the master password turns it off and says so. Moving the data folder doesn't.
- Say what the trade-off is before the user agrees to it.

What enrollment changes do differs by platform, and the setup window says so:
- **Windows:** the Hello key belongs to the Windows account, not to particular fingers or faces. Adding a face or
  finger, or changing the Hello PIN, doesn't turn it off; whatever Hello accepts for the account opens the vault.
- **Mac:** the keychain item is bound to the fingers enrolled when it was made. Adding or removing one makes it
  unreadable for good, so it stops working until it's turned on again.

**Non-goals**
- Linux. fprintd and polkit only answer "the user said yes"; there's no key that only the fingerprint reader can
  release. That is the bare consent prompt §4 rules out, so Linux doesn't offer it.
- The OS keychain store. It already opens with the user's login.
- Recovery. A forgotten master password still can't be recovered: biometric unlock is a second way in on one computer,
  not a backup.
- Remote sessions. It's for the person at the computer (§2.4).

---

## 2. Key hierarchy

Today (§3.3 of the storage design): master password → Argon2id → key-encryption key, which unwraps the random data key
(DEK); the DEK encrypts the credentials.

Biometric unlock adds a second, separate wrap of the same DEK:

```
authenticator secret ──HKDF-SHA256──► biometric wrapping key ──XChaCha20-Poly1305──► DEK
(only the platform authenticator can produce it)
```

The vault file isn't touched. The second wrap lives in a small record kept per user on this computer, outside the data
folder, and the authenticator secret never exists anywhere on disk.

### 2.1 Windows

- **Key:** `KeyCredentialManager.RequestCreateAsync` makes a Windows Hello key named `Orrery vault <id>` for the
  signed-in user (§2.3 for the id). Every use needs a Hello gesture (face, fingerprint or PIN) that Windows checks
  itself; Orrery only gets the result.
- **Challenge:** 32 random bytes, made when the feature is turned on and kept in the record. Not secret.
- **Authenticator secret:** Hello's signature over the challenge. Hello keys are RSA and sign with RSASSA-PKCS1-v1_5
  and SHA-256, which is deterministic, so the same key and challenge always give the same signature. When turning it
  on, Orrery checks that the key is RSA and that the signature verifies against the key's public half, and refuses
  anything else: a randomized signature would never open the vault again. Secrets shorter than 32 bytes are refused.
  The signature is copied out of Windows' buffer through `IBufferByteAccess` and the buffer wiped before it's released.
- **The key must be in the TPM.** Windows can keep a Hello key in software, protected only by the PIN, even on a
  computer with a TPM (when the TPM isn't ready, or policy allows software keys). Someone with the disk could guess a
  short PIN offline far faster than an Argon2id master password. So:
  - Settings doesn't offer it without a TPM 2.0 (`Tbsi_GetDeviceInfo`). That's only a quick hint.
  - The proof is per key: straight after making it, Orrery calls `KeyCredential.GetAttestationAsync` (IKeyCredential
    slot 10). Only `Success`, which Windows gives for a key the TPM attests, is accepted. `NotSupported` is refused.
    It doesn't prove the key is in software: Microsoft only says attestation might not be available, and a computer
    whose Hello log records the key as a hardware key, with a ready TPM and its attestation certificate present, has
    answered it. The refusal message must never suggest resetting or removing the Windows PIN. Doing so deletes every
    Hello key on the account, passkeys and other apps' keys included, and it didn't help on that computer. `TemporaryFailure` (usually Windows hasn't yet fetched the TPM's attestation certificate, which
    needs the internet) and `UnknownError` are refused too, with "try again later". In every refusal the new key is
    deleted and nothing is saved.
  - Why not fall back to the key's NCrypt implementation type (`NCRYPT_IMPL_TYPE_PROPERTY` through the Microsoft
    Passport Key Storage Provider) on a temporary failure: opening a `KeyCredentialManager` key there needs its
    internal key name, which Microsoft doesn't document for these keys, so it couldn't be checked without a device and
    might silently match the wrong thing. A refusal that clears itself later is the safer of the two.
  - `BiometricUnlock.TurnOnAsync` enforces it as well: it refuses any new key the authenticator doesn't report as
    hardware-backed, and checks availability (including §2.4) before asking for anything.
- **Record:** a generic credential in Windows Credential Manager, `orrery:biometric:<id>`, stored for this computer
  only (it doesn't roam with the profile). Windows protects it with DPAPI under the user's login.
- **Prompt:** on Windows 11 24H2 and later the Hello prompt belongs to Orrery's window
  (`RequestCreateForWindowAsync`, `RequestSignForWindowAsync`). Orrery asks again without the window only when Windows
  refuses the call as it's made (no such interface or class, `E_NOTIMPL`, `E_NOINTERFACE`, `REGDB_E_CLASSNOTREG`) or
  refuses the window (`E_INVALIDARG`, or `ERROR_INVALID_WINDOW_HANDLE` as `0x80070578`), straight away or when the call
  finishes. Any other error is the answer: the user may already have seen a prompt, and shouldn't get a second. Older
  builds use the original calls, and Orrery brings
  the prompt to the front itself: only a visible window of class `Credential Dialog Xaml Host` whose process is
  `%SystemRoot%\System32\CredentialUIBroker.exe`, never another program's window of that class.
- `Windows.UI.dll` and `tbs.dll` are only loaded from System32.

### 2.2 macOS (written, not yet run on a Mac)

- **Authenticator secret:** 32 random bytes in a data-protection keychain item with access control
  `kSecAccessControlBiometryCurrentSet` and `kSecAttrAccessibleWhenPasscodeSetThisDeviceOnly`. The keychain hands it
  over only after Touch ID matches a finger enrolled now, and the Secure Enclave enforces that. There's no passcode
  fallback. Adding or removing a finger makes the item unreadable for good.
- **Unreadable item:** Apple's documents say the item becomes inaccessible without naming the status. Reports differ
  between `errSecItemNotFound` and `errSecAuthFailed`, so both are treated as "key not found" (§3.4), which never
  deletes anything by itself.
- **Canceling:** the read runs under an `LAContext`; closing the unlock window calls `invalidate` on it.
- **Clearing the secret:** Orrery copies the secret out of the `CFData` the keychain returns and clears its own copy
  when done. That `CFData` is immutable: CoreFoundation has no call to clear one, and writing through its read-only
  pointer isn't allowed, so it's released without being wiped. The copy Orrery hands the keychain when turning it on
  is a `CFMutableData`, cleared before it's released.
- **Record:** the wrapped DEK, in the login keychain beside saved credentials (service "Orrery"). Records are listed
  with `SecItemCopyMatching` for attributes only, which reads no secret.
- **Signing:** the data-protection keychain only serves apps signed with a `keychain-access-groups` entitlement. Orrery's
  macOS builds aren't signed yet, so until they are, the keychain refuses the item (`errSecMissingEntitlement`) and
  Settings says Touch ID unlock needs a signed copy of Orrery.

### 2.3 The record and the vault's identity

```json
{
  "format": "orrery-biometric",
  "version": 2,
  "authenticator": "windows-hello",
  "keyName": "Orrery vault 1f0c…",
  "challenge": "<b64>",
  "vault": "<b64>",
  "nonce": "<b64>",
  "ct": "<b64>",
  "place": "9a41…"
}
```

- **Vault fingerprint** (`vault`): SHA-256 over the vault's key block (KDF settings, salt and wrapped key). Ordinary saves
  don't change it; every master password change does, since that makes a new salt and a new DEK. It's the same
  wherever the file is.
- **`<id>`** (record name and Hello key name): the first 32 hex digits of SHA-256 over
  `orrery-biometric|vault-id|<fingerprint>`. So the record follows the vault, not its path: Move data, a renamed folder
  or a new drive letter change nothing, and an installed vault and a portable one (different vaults) each get their
  own record and key. After a master password change no record has the new id, so it's off.
- **`place`**: SHA-256 of the vault file's full path (upper-cased on Windows), first 32 hex digits. Only a hint for
  noticing a password change made elsewhere (§3.4); updated after each biometric unlock, and left out of the associated
  data so it can change. A path says nothing about which vault is there, so a record found only by its `place` is
  never removed by itself.
- **Wrapping key:** HKDF-SHA256 with the authenticator secret as input, the challenge as salt and
  `orrery-biometric|v2|<authenticator>|<keyName>` as info.
- **Associated data:** format, version, authenticator, key name, challenge and vault fingerprint, so a record edited or
  copied to another vault's slot fails to decrypt instead of opening the wrong thing. Orrery also checks the record's
  key name and fingerprint match its name before asking for a gesture.
- **Records for this vault** are found by listing `orrery:biometric:*` (`CredEnumerateW` on Windows): the one named for
  the current key block, plus any whose fingerprint is one this vault had before a password change made in this run of
  Orrery. Those are certainly this vault's, and are removed when stale. A record whose `place` is this vault's path but
  whose fingerprint this run never saw is only probably this vault's: another copy of Orrery may have changed the
  password, or a different vault may have been made where this one was (and this one may come back from a backup). It's
  left alone, and only means the feature reads as off here (§3.4), until the user removes it (§3.5) or turns the
  feature off.
- **Keys waiting to be deleted:** when a stale record is removed without the user (§3.4), its Hello key's name goes in
  `orrery:biometric-retired-keys`, and those keys are deleted the next time the user turns the feature on or off. A
  record found only by its `place` never puts its key there.
- **Other versions:** a record whose `version` isn't 2 (from a newer Orrery, say) is left alone and reads as off in this
  build. Only a record that doesn't parse, or claims version 2 and doesn't fit, is removed. (§2.5 covers version 1
  records found by the old path-based names.)

### 2.4 Remote sessions

Inside a Remote Desktop session (`GetSystemMetrics(SM_REMOTESESSION)`, or any session other than the one attached to the
physical console, which catches RemoteFX sessions too) Orrery doesn't offer Windows Hello, doesn't prompt, and can't
turn it on. The unlock window says "This is a remote session. Windows Hello unlock only works at the computer itself,
so use the master password." It stays on for when the user is back at the computer, and can still be turned off.
Otherwise someone with the account password and the Hello PIN could open the vault from anywhere.

### 2.5 Version 1 records

The first builds named the record and key after the vault's path (`version: 1`). They're never used: a version 1 record
found for this vault (by its path, or by its fingerprint) is deleted and the user told "Windows Hello unlock was set up
by an earlier test version of Orrery, so it was turned off". Its Hello key is deleted the next time the user turns the
feature on or off. These only exist on test machines; the feature hasn't shipped.

---

## 3. Flows

### 3.1 Turning it on
Settings › Credentials › *Unlock with Windows Hello* (or *Touch ID*). The setup window first says what will open the
vault from now on (§1). Orrery checks it's available here (TPM, Hello set up, not a remote session), then asks for the
master password and checks it against the file, with the same slow-down after wrong guesses as the unlock window. Then
it makes a new key, checks the TPM attests it (Windows), shows the system prompt (twice on Windows: once to make the
key, once to sign with it; on a Mac once, to read the item straight back), and writes the record. Any earlier records
and keys for this vault are deleted. The vault file is unchanged.

### 3.2 Unlocking
When it's on, the unlock window shows the system prompt as it opens and keeps the password field below it. Before
prompting, Orrery removes stale records for this vault, so a password change is reported without asking for a finger.
After the prompt: secret → wrapping key → DEK → contents. Canceling leaves the password field.

- **Cancel cancels.** The Cancel button stays usable while the prompt is up. Cancel, Escape or closing the window
  cancels the prompt (`IAsyncInfo.Cancel` on Windows); an answer that arrives anyway is thrown away, and if the vault
  opened in the meantime it's locked again.
- **One prompt at a time.** Callers that ask for the vault while an unlock window is open wait for that window rather
  than opening another, and Orrery never runs two prompts at once. They get the window's answer: if it was canceled
  just as the prompt opened the vault, the vault is locked again before any of them carries on, and each hears no.

### 3.3 Turning it off
Clearing the setting deletes this vault's record and Hello key or keychain item, any records and keys left from before a
master password change or by the first test builds, any record (and its key) found only by this vault's path, and the
keys waiting to be deleted (§2.3). Turning it on again doesn't remove a record found only by the path, since it might be
another vault's.

### 3.4 When it stops working

The Hello key is only ever deleted when the user turns the feature off, turns it on again (which replaces it), or
removes an old setup (§3.5).
Nothing that might be temporary deletes anything.

| Event | When Orrery notices | What happens | What the user sees |
|---|---|---|---|
| Master password changed in Orrery | Straight away | Records and keys for the old and new key blocks deleted | The change confirmation says Windows Hello unlock was turned off |
| Master password changed by another copy of Orrery | Next unlock, or when Settings opens | Nothing deleted: the old record is only found by `place`, which could be another vault's (§2.3). It and its key stay until the user removes them (§3.5) or turns the feature off | "… unlock was set up for a vault that used to be at this location, or for this vault before another copy of Orrery changed its master password, so it's off for this vault", each time until it's turned on again; Settings offers to remove the old setup |
| A different vault made at the same path | As above | As above: the first vault's record and key are kept, and it opens with them if it's put back | As above: Orrery can't tell these two apart, so the message names both |
| The record no longer decrypts | Next unlock | Record deleted; its key waits for the user | "The key saved for … no longer matches …" |
| Windows Hello can't find the key (Hello reset, PIN removed, or something temporary) | Next unlock | Nothing deleted; not offered again until Orrery restarts | "Windows Hello couldn't find Orrery's key. Use the master password this time. If this keeps happening, turn … off and on again in Settings." |
| Touch ID fingers added or removed | Next unlock | As above | As above |
| A remote session | Every time | Nothing changes | §2.4 |
| Prompt canceled, or the sensor fails once | Never | Nothing changes | Type the password |
| Hello enrollment changes on Windows (new PIN, face or finger) | Never | Nothing changes: it keeps working | Nothing; the setup window said so |

Each message ends with how to turn it on again.

**What a record left by another copy's password change still does.** It can't open the vault as it is now: the change
made a new data key, and the record holds the old one. But it still holds the old data key, so a backup or other copy
of the vault from before the change, with the old key block, still opens with Windows Hello or Touch ID on this
computer until the user removes it (§3.5) or turns the feature off. Turning it on alone doesn't remove it. That's no more
than the old master password opens, and it still needs a Hello or Touch ID gesture at this computer, but a user who
changed the password because the old one leaked, and keeps old copies, should remove it. When the change is made in
this copy of Orrery, the old record and key are deleted straight away.

### 3.5 Removing an old setup

When a record found only by the vault's path is saved, Settings › Credentials shows, under the switch, "A Windows Hello
setup is still saved for a vault that used to be at this location, or for this vault before another copy of Orrery
changed its master password", and a *Remove old Windows Hello setup* button (*Touch ID* on a Mac), named the same for
screen readers. It asks first: the old vault that used to be there, and any backup of this vault from before its
password was changed, won't open with Windows Hello any more (they still open with their master password), and this
vault isn't affected. Confirmed, it deletes those records and their Hello keys or keychain items straight away, and
nothing else. It works whether or not the feature is on for this vault.

### 3.6 Move data and the old copy

The record follows the vault's key block, so after Move data Windows Hello unlock carries on with the moved vault,
and Move data says so. The old copy is the same vault with the same key, so until it's deleted biometric unlock opens it
too; the offer to delete the old copy says that when the feature is on. Switching from the vault file to the system
keychain turns it off, since the vault file stays behind.

---

## 4. What an attacker can and can't do

| Attacker | Can | Can't |
|---|---|---|
| Has a copy of the data folder (USB stick, synced folder, backup) | Try master passwords against `vault.frv`, exactly as today | Use biometric unlock: nothing for it is in the folder, and the vault file is byte-for-byte what it would be without it |
| Has the whole disk or user profile, offline | Read the encrypted Credential Manager store | Decrypt the record without the user's Windows password (DPAPI), and even with it, produce the signature: the Hello private key is attested to be in the TPM, which limits PIN guesses itself. On a Mac, the item's key stays in the Secure Enclave |
| Another local user | Nothing new | Read this user's Credential Manager or keychain, or use their Hello key; both are per user |
| Someone in a Remote Desktop session as the user | Nothing new | Use Windows Hello unlock: it isn't offered or prompted for remotely (§2.4) |
| A lower-integrity process of the same user (a sandboxed browser renderer) | At most, raise a Windows Hello prompt the user didn't ask for, if it can reach `KeyCredentialManager` at all | Get the signature without the user approving that prompt. AppContainer apps get their own Hello keys and can't open Orrery's. Not yet tested; §6.3 says how |
| Malware running as the user | What it can do today: read memory, log the master password as it's typed, or show a prompt the user approves | (Accepted risk: same-user code execution) |
| Someone at the user's unlocked session who knows their Windows Hello PIN | Open the vault | Open it on another computer |

The last row is the one real change for someone at the keyboard: with Windows Hello unlock on, the Windows Hello PIN
(or any face or finger enrolled for the account) opens the vault on this computer, and a PIN is often shorter than a
master password. The setup window and Settings say so. Touch ID has no passcode fallback, so on a Mac only an enrolled
finger opens it.

### Why not a plain "is it you?" prompt

`UserConsentVerifier` on Windows and `LAContext.evaluatePolicy` on macOS only return yes or no. If the key that opens
the vault sat in a file the user can read, any program running as that user, or anyone with the disk and the user's
password, could take the key and skip the prompt. Here the step that releases the key is done by the authenticator:
without a matching gesture there is no signature, and on a Mac no secret.

---

## 5. Compared with the master password alone

| | Password only | With biometric unlock |
|---|---|---|
| Stolen data folder | Argon2id against the master password | The same |
| Stolen disk, offline | The same, plus anything else the attacker learns | The same: the record also needs DPAPI and the TPM or Secure Enclave key |
| Another local user | Nothing | Nothing |
| At the unlocked session | Needs the master password | Windows: the Hello PIN, face or finger also works. Mac: an enrolled finger |
| Forgotten master password | Vault lost | Still opens on this computer while biometric unlock stays on; the master password can't be changed without the current one, so this isn't a recovery route |
| Master password changed | Old copies open with the old password | Biometric unlock turns off; the old record can't open anything saved after the change, since the DEK changed |

---

## 6. How it's built

- `Orrery.Core/Credentials/BiometricUnlock.cs`: the record, the vault's identity, the crypto and the turn-off rules,
  behind `IPlatformAuthenticator` (make a key, release the secret, delete), `ISecretBackend` (where the record lives,
  and listing records) and `IRemoteSession`. Unit-tested with a fake authenticator, an in-memory record store and a
  fake remote session.
- `VaultFileStore` gains internal methods: the key-block fingerprint, the fingerprints it had before password changes
  made through it, the DEK for a checked master password, and unlocking with a DEK (locked again if canceled).
  Nothing about the file format changes.
- `WindowsHelloAuthenticator.cs` and `WinRt.cs`: Windows Hello through `KeyCredentialManager`.
- `RemoteSession.cs`: the Remote Desktop check.
- `MacTouchIdAuthenticator.cs`: the access-controlled keychain item, through Security.framework and LocalAuthentication.

### 6.1 Reaching WinRT from a cross-platform build

Orrery targets plain `net10.0` and ships one portable build per runtime. The options for calling `KeyCredentialManager`:

| Option | Cost |
|---|---|
| A Windows target framework (`net10.0-windows10.0.19041.0`) | Splits `Orrery.Core` and `Orrery.App` into two targets, changes every packaging script and lock file, and ships `Microsoft.Windows.SDK.NET.dll` (about 25 MB) with every Windows build |
| CsWinRT | A build-time code generator and the `WinRT.Runtime` dependency, for a handful of calls |
| Calling the WinRT ABI directly | A few hundred lines of interop to get right |

Orrery calls the ABI directly: `RoGetActivationFactory`, then the methods by their vtable slot, with every interface ID
and slot checked against the Windows SDK 10.0.26100 headers. No new package, no target change, and nothing loads on
Linux or macOS. Async calls are waited on by polling `IAsyncInfo.Status` on a dedicated thread, which avoids writing a
COM callback object; the wait checks a cancellation token on each poll and calls `IAsyncInfo.Cancel` when it fires. A
getter that succeeds with a null object is treated as `E_POINTER` rather than called through. `Orrery.Core` allows
unsafe code, as `Orrery.App` and the protocol projects already do.

### 6.2 Tests

No test makes a real Hello key, keychain item or Credential Manager entry.

- `BiometricUnlockTests`: turning on, unlocking, wrong master password, canceling (before, during and after the
  prompt answers), one prompt at a time, every case in §3.4, the vault file moving, a different drive root, Move data,
  version 1 records, records from a newer version, records found only by the vault's path (another copy's password
  change, a different vault made in the same place) and removing them, remote sessions, keys not shown to be in hardware, short secrets, records edited or moved to
  another vault, an old record that decrypts but whose data key no longer opens the vault, and that turning it on
  leaves the data folder untouched.
- `WindowsHelloTests` (Windows only, no prompt): the WinRT plumbing for real (activation, strings, buffers, wiping a
  buffer, waiting on an async call), the availability check, the attestation decision for each status, which errors
  from a 24H2 "ForWindow" call mean asking again without the window, and which window is brought forward. `DeterministicSignatureTests`: the RSA check over public-key blobs made in the test.
- `SecretLeakTests`: master passwords, authenticator secrets and the vault's data keys stay out of the log.
- Headless UI tests for the unlock window (cancel, closing, one window for several callers and what they hear when it's
  canceled just as the vault opened, remote sessions, a key not
  found), the setup window (the trade-off, the password box, automation names) and the Settings switch (rechecked
  after each change, the password change, the store switch), with a fake authenticator.

### 6.3 What needs a person

The Hello and Touch ID prompts can't be driven by a test. On Windows: turn it on, unlock with face, finger and PIN,
cancel, close the window with the prompt up, reset the Hello PIN and unlock again, check the prompt sits in front of
Orrery on 24H2 and on an older build, and check what `GetAttestationAsync` returns on a machine with a TPM, one with a
TPM that's offline (expect `TemporaryFailure`) and one where Hello uses software keys (expect `NotSupported`). Open
Orrery over Remote Desktop and check it falls back to the password. On a Mac, everything in §2.2, starting with a
signed build.

**A lower-integrity process.** Untested: whether a low-integrity or AppContainer process of the same user can open
Orrery's Hello key by name with `KeyCredentialManager.OpenAsync`, or read the record with `CredReadW`. A safe way to
check, on a test account with throwaway data:
1. Turn on Windows Hello unlock for a vault holding only dummy credentials.
2. Build a small console tool that calls `CredReadW("orrery:biometric:<id>")` and `KeyCredentialManager.OpenAsync(
   "Orrery vault <id>")`, then `RequestSignAsync` over a dummy challenge, and reports each status. It never writes or
   deletes anything.
3. Run it at medium integrity first, to see the expected results. Then at low integrity: copy it, mark the copy
   `icacls tool.exe /setintegritylevel low`, and start it from a low-integrity shell. Then in an AppContainer, for
   example by launching it through a test AppContainer profile (`CreateAppContainerProfile` and a launcher), or as a
   packaged sideloaded app.
4. Expect `CredReadW` to fail and `OpenAsync` to return `NotFound`, or at worst show a Hello prompt the user must
   approve. Delete the tool, the dummy vault and the test profile afterwards, and turn the feature off in Orrery.
