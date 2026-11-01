# Orrery design: unlocking the vault with Windows Hello or Touch ID

Status: **Draft**, 2026-10-31
Scope: opening `vault.frv` with Windows Hello or Touch ID instead of typing the master password. Builds on §3 of
[design-storage-vault-import.md](design-storage-vault-import.md).

---

## 1. Goals and non-goals

**Goals**
- Open the vault with a face, fingerprint or Windows Hello PIN on Windows, and Touch ID on macOS.
- A stolen data folder is no easier to open than it is with the master password alone.
- The master password always still works. Turning this on never changes the vault file.
- Changing the master password, or the system reporting that its enrolled fingers or faces changed, turns it off and
  says so.

**Non-goals**
- Linux. fprintd and polkit only answer "the user said yes"; there's no key that only the fingerprint reader can
  release. That is the bare consent prompt §4 rules out, so Linux doesn't offer it.
- The OS keychain store. It already opens with the user's login.
- Recovery. A forgotten master password still can't be recovered: biometric unlock is a second way in on one computer,
  not a backup.

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
  signed-in user. Windows creates Hello keys in the TPM and never lets the private key out. Every use needs a Hello
  gesture (face, fingerprint or PIN) that Windows checks itself; Orrery only gets the result.
- **Challenge:** 32 random bytes, made when the feature is turned on and kept in the record. Not secret.
- **Authenticator secret:** Hello's signature over the challenge. Hello keys are RSA and sign with RSASSA-PKCS1-v1_5
  and SHA-256, which is deterministic, so the same key and challenge always give the same signature. When turning it
  on, Orrery checks that the key is RSA and that the signature verifies against the key's public half, and refuses
  anything else: a randomised signature would never open the vault again.
- **TPM required:** without a TPM 2.0, Windows keeps Hello keys in software, protected by the PIN. Someone with the
  disk could guess a short PIN offline far faster than an Argon2id master password, so Orrery doesn't offer Windows
  Hello unlock on such a machine (checked with `Tbsi_GetDeviceInfo`). The API doesn't say whether a particular key is in
  the TPM, so this relies on Windows using the TPM when one is present, which is its documented behaviour.
- **Record:** a generic credential in Windows Credential Manager, `orrery:biometric:<id>`, stored for this computer
  only (it doesn't roam with the profile). Windows protects it with DPAPI under the user's login.
- **Prompt:** on Windows 11 24H2 and later the Hello prompt belongs to Orrery's window
  (`RequestCreateForWindowAsync`, `RequestSignForWindowAsync`). Older builds use the original calls and Orrery brings
  the prompt to the front itself.

### 2.2 macOS (written, not yet run on a Mac)

- **Authenticator secret:** 32 random bytes in a data-protection keychain item with access control
  `kSecAccessControlBiometryCurrentSet` and `kSecAttrAccessibleWhenPasscodeSetThisDeviceOnly`. The keychain hands it
  over only after Touch ID matches a finger enrolled now, and the Secure Enclave enforces that. There's no passcode
  fallback. Adding or removing a finger makes the item unreadable for good.
- **Record:** the wrapped DEK, in the login keychain beside saved credentials (service "Orrery").
- **Signing:** the data-protection keychain only serves apps signed with a `keychain-access-groups` entitlement. Orrery's
  macOS builds aren't signed yet, so until they are, the keychain refuses the item (`errSecMissingEntitlement`) and
  Settings says Touch ID unlock needs a signed copy of Orrery.

### 2.3 The record

```json
{
  "format": "orrery-biometric",
  "version": 1,
  "authenticator": "windows-hello",
  "keyName": "Orrery vault 1f0c…",
  "challenge": "<b64>",
  "vault": "<b64>",
  "nonce": "<b64>",
  "ct": "<b64>"
}
```

- `<id>` is the first 32 hex digits of SHA-256 over the vault file's full path, so an installed vault and a portable
  one each get their own record and Hello key.
- **Wrapping key:** HKDF-SHA256 with the authenticator secret as input, the challenge as salt and
  `orrery-biometric|v1|<authenticator>|<keyName>` as info.
- **Vault fingerprint** (`vault`): SHA-256 over the vault's key block (KDF settings, salt and wrapped key). Ordinary saves
  don't change it; every master password change does, since that makes a new salt and a new DEK.
- **Associated data:** format, version, authenticator, key name, challenge and vault fingerprint, so a record edited or
  copied to another vault's slot fails to decrypt instead of opening the wrong thing.

---

## 3. Flows

### 3.1 Turning it on
Settings › Credentials › *Unlock with Windows Hello* (or *Touch ID*). Orrery asks for the master password and checks it
against the file, with the same slow-down after wrong guesses as the unlock window. Then it makes a new key and
challenge, shows the system prompt (twice on Windows: once to make the key, once to sign with it; on a Mac once, to read
the item straight back), and writes the record. The vault file is unchanged.

### 3.2 Unlocking
When it's on, the unlock window shows the system prompt as it opens and keeps the password field below it. Before
prompting, Orrery compares the vault fingerprint with the record, so a stale record is turned off without asking for a
finger. After the prompt: secret → wrapping key → DEK → contents. Cancelling leaves the password field.

### 3.3 Turning it off
Clearing the setting deletes the record and the Hello key or keychain item.

### 3.4 When it turns itself off

| Event | When Orrery notices | What the user sees |
|---|---|---|
| Master password changed in Orrery | Straight away | The change confirmation says Windows Hello unlock was turned off |
| Master password changed by another copy of Orrery | Next unlock, or when Settings opens | "The master password was changed, so … was turned off" |
| Windows Hello reset, or its PIN removed (key not found) | Next unlock | "Windows Hello no longer has Orrery's key …" |
| Touch ID fingers added or removed (item gone) | Next unlock | "The fingers enrolled in Touch ID changed …" |
| The record no longer decrypts | Next unlock | "The saved key no longer matches …" |
| Prompt cancelled, or the sensor fails once | Never | Nothing changes; type the password |

Each message ends with how to turn it on again. Orrery deletes the record and the key in every case except the last.

---

## 4. What an attacker can and can't do

| Attacker | Can | Can't |
|---|---|---|
| Has a copy of the data folder (USB stick, synced folder, backup) | Try master passwords against `vault.frv`, exactly as today | Use biometric unlock: nothing for it is in the folder, and the vault file is byte-for-byte what it would be without it |
| Has the whole disk or user profile, offline | Read the encrypted Credential Manager store | Decrypt the record without the user's Windows password (DPAPI), and even with it, produce the signature: the Hello private key stays in the TPM, which limits PIN guesses itself. On a Mac, the item's key stays in the Secure Enclave |
| Another local user | Nothing new | Read this user's Credential Manager or keychain, or use their Hello key; both are per user |
| A lower-integrity process of the same user (a sandboxed browser renderer) | At most, raise a Windows Hello prompt the user didn't ask for, if it can reach `KeyCredentialManager` at all | Get the signature without the user approving that prompt. AppContainer apps get their own Hello keys and can't open Orrery's. Not yet tested against a low-integrity process |
| Malware running as the user | What it can do today: read memory, log the master password as it's typed, or show a prompt the user approves | (Accepted risk: same-user code execution) |
| Someone at the user's unlocked session who knows their Windows Hello PIN | Open the vault | Open it on another computer |

The last row is the one real change for someone at the keyboard: with Windows Hello unlock on, the Windows Hello PIN
opens the vault on this computer, and a PIN is often shorter than a master password. Settings says so. Touch ID has no
passcode fallback, so on a Mac only an enrolled finger opens it.

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

- `Orrery.Core/Credentials/BiometricUnlock.cs`: the record, the crypto and the turn-off rules, behind
  `IPlatformAuthenticator` (make a key, release the secret, delete) and `ISecretBackend` (where the record lives).
  Unit-tested with a fake authenticator and an in-memory record store.
- `VaultFileStore` gains three internal methods: the key-block fingerprint, the DEK for a checked master password, and
  unlocking with a DEK. Nothing about the file format changes.
- `WindowsHelloAuthenticator.cs` and `WinRt.cs`: Windows Hello through `KeyCredentialManager`.
- `MacTouchIdAuthenticator.cs`: the access-controlled keychain item, through Security.framework and LocalAuthentication.

### 6.1 Reaching WinRT from a cross-platform build

Orrery targets plain `net10.0` and ships one portable build per runtime. The options for calling `KeyCredentialManager`:

| Option | Cost |
|---|---|
| A Windows target framework (`net10.0-windows10.0.19041.0`) | Splits `Orrery.Core` and `Orrery.App` into two targets, changes every packaging script and lock file, and ships `Microsoft.Windows.SDK.NET.dll` (about 25 MB) with every Windows build |
| CsWinRT | A build-time code generator and the `WinRT.Runtime` dependency, for five calls |
| Calling the WinRT ABI directly | A few hundred lines of interop to get right |

Orrery calls the ABI directly: `RoGetActivationFactory`, then the methods by their vtable slot, with every interface ID
and slot checked against the Windows SDK 10.0.26100 headers. No new package, no target change, and nothing loads on
Linux or macOS. Async calls are waited on by polling `IAsyncInfo.Status` on a dedicated thread, which avoids writing a
COM callback object. `Orrery.Core` now allows unsafe code, as `Orrery.App` and the protocol projects already do.

### 6.2 Tests

- `BiometricUnlockTests`: turning on, unlocking, wrong master password, cancelling, every turn-off case in §3.4,
  records that are edited or moved to another vault, and that turning it on leaves the data folder untouched.
- `WindowsHelloTests` (Windows only, no prompt): the WinRT plumbing for real (activation, strings, buffers, waiting on
  an async call) and the availability check.
- `SecretLeakTests`: master passwords and authenticator secrets stay out of the log.
- Headless UI tests for the unlock window and the Settings switch, with a fake authenticator.

### 6.3 What needs a person

The Hello and Touch ID prompts can't be driven by a test. On Windows: turn it on, unlock with face, finger and PIN,
cancel, reset the Hello PIN and unlock again, check the prompt sits in front of Orrery on 24H2 and on an older build.
On a Mac, everything in §2.2, starting with a signed build.
