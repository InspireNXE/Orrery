# Orrery design: storage modes, credential vault, mRemoteNG import

Status: **Draft**, 2026-10-29
Scope: where Orrery keeps its data (installed vs portable), how secrets are stored, and how `confCons.xml` from mRemoteNG is imported.

Related decisions (already made):
- Cross-platform rebuild on Avalonia; RDP via IronRDP behind a session abstraction.
- Importing mRemoteNG connections is a **must-have** for v1.
- Both **installed** and **portable** distributions ship from day one.

---

## 1. Goals and non-goals

**Goals**
- One code path for data access, with the *location* decided once at startup.
- A portable copy runs from a USB stick on Windows, macOS or Linux and carries its connections and secrets with it.
- Secrets never appear in the connections file; connections reference credentials by id.
- An mRemoteNG user can import their whole tree, passwords included, in one step, and re-import without creating duplicates.

**Non-goals (v1)**
- Sync between machines (a user can point a custom data folder at a synced drive; no merge logic).
- Importing mRemoteNG's SQL Server backend, `extApps.xml` external tools, or PuTTY registry sessions (see §4.8).
- Team or shared vaults.

---

## 2. Storage modes

### 2.1 Mode detection

At startup, before anything else touches disk:

1. Resolve the **app directory**: the folder that holds the executable. On macOS, the folder that holds the `.app` bundle (never inside the bundle; it is signed and may be read-only).
2. If `orrery.portable.json` exists in the app directory, run in **portable** mode. Otherwise run in **installed** mode.

Installers never ship the marker. The portable zip/tarball/AppImage bundle ships it with no `dataLocation` set, which triggers the first-run prompt.

Marker file:

```json
{
  "version": 1,
  "dataLocation": "app",        // "app" | "user" | "custom" | absent = ask on launch
  "customPath": "../RemoteData" // only for "custom"; relative paths resolve from the app directory
}
```

Relative `customPath` is deliberate: a USB stick mounts as `E:` on one machine and `F:` on the next, or under a different `/media/...` path on Linux.

### 2.2 Portable first launch

When `dataLocation` is absent, show a dialog before the main window:

| Option | Data goes to | When to choose it |
|---|---|---|
| **With the app** (default) | `<app dir>/data/` | Carry everything on a USB stick |
| **This computer's user profile** | Per-OS user path (§2.3) under a `Portable` profile | App runs from anywhere, data stays on this machine |
| **Custom folder…** | Picked folder | e.g. a synced or network drive |

Rules:
- Before accepting "With the app" or a custom folder, **probe write access** (create and delete a temp file). It fails on read-only media, `Program Files`, and macOS App Translocation (an app run straight from a downloaded, quarantined location). Explain the failure and offer the other options.
- Write the choice back to the marker file. If the marker itself is not writable, keep the choice in memory for this session and warn that the prompt will reappear.
- If a data folder already exists at the chosen location, use it and don't re-initialise it.
- Portable mode always uses the **vault file** for secrets (§3), because the OS keychain does not travel.

The "user profile" option uses a `Portable` subfolder (e.g. `%APPDATA%\Orrery\Portable`) so it never collides with an installed copy on the same machine.

### 2.3 Installed paths

| | Config + connections + vault | Cache | Logs |
|---|---|---|---|
| Windows | `%APPDATA%\Orrery` | `%LOCALAPPDATA%\Orrery\Cache` | `%LOCALAPPDATA%\Orrery\Logs` |
| macOS | `~/Library/Application Support/Orrery` | `~/Library/Caches/Orrery` | `~/Library/Logs/Orrery` |
| Linux | `$XDG_CONFIG_HOME/orrery` (default `~/.config`) | `$XDG_CACHE_HOME/orrery` | `$XDG_STATE_HOME/orrery/logs` |

In portable mode, cache and logs live under the data folder (`cache/`, `logs/`) so nothing is left on the host machine.

### 2.4 Data folder layout

```
<data>/
  settings.json          app preferences (theme, accent, shortcuts, window state)
  connections.json       connection tree; references credentials by id, no secrets
  vault.frv              encrypted vault (always in portable mode; optional when installed)
  backups/               rotating copies of connections.json and vault.frv
  import/                import reports (§4.7)
  cache/  logs/          portable mode only
  .lock                  single-instance lock for this data folder
```

### 2.5 Abstraction

```csharp
public interface IDataLocation
{
    StorageMode Mode { get; }          // Installed | Portable
    string ConfigDir { get; }
    string CacheDir { get; }
    string LogDir { get; }
    bool UsesOsKeychain { get; }       // false in portable mode
}
```

Resolved once in `Program.Main` and registered as a singleton. No other code computes paths.

### 2.6 Write safety
- **Atomic writes:** write `file.tmp`, flush to disk, then rename over the original. Never write in place.
- **Backups:** before each save of `connections.json` or `vault.frv`, copy the previous version into `backups/`. Keep the last 10 plus one per day for 7 days.
- **Single instance per data folder:** hold an exclusive lock on `.lock`. A second instance pointed at the same folder brings the first one to the front instead of opening.
- **Schema versioning:** every JSON file carries `"schemaVersion"`. Migrations run on load, after a backup.

### 2.7 Changing location later
Settings › Storage shows the current mode and path and offers **Move data…**. It copies the data, verifies it (hashes match), updates the marker file in portable mode, and then offers to delete the old copy. Switching between installed and portable is a different binary, so it means exporting and then importing, not moving.

---

## 3. Credential vault

### 3.1 Model

```
Credential
  id            GUID
  name          display name ("corp admin")
  kind          Password | SshKey | SshKeyWithPassphrase
  username, domain
  secret        password / private key / passphrase   (never stored in connections.json)
  notes
```

Connections hold `credentialId` (or inherit it from a folder). Several connections share one credential, so a password rotation is a single edit.

### 3.2 Stores

```csharp
public interface ICredentialStore
{
    bool IsLocked { get; }
    Task UnlockAsync(UnlockRequest request);
    Task<Credential?> GetAsync(Guid id);
    Task SaveAsync(Credential credential);
    Task DeleteAsync(Guid id);
    IAsyncEnumerable<CredentialSummary> ListAsync();   // metadata only, no secrets
}
```

Two implementations:

| Store | Used when | Notes |
|---|---|---|
| `OsKeychainStore` | Installed mode, by default | Windows Credential Manager, macOS Keychain, Secret Service (libsecret) on Linux. Each credential is one keychain item keyed `orrery:<id>`. Metadata (name, username, kind) lives in `connections.json`. |
| `VaultFileStore` | Portable mode (always); installed mode if the user opts in; Linux when no Secret Service is running (headless, minimal window managers) | Single encrypted file `vault.frv`, unlocked with a master password. |

Offering the vault file in installed mode as well gives users one encrypted file to back up and move.

### 3.3 Vault file format

```
vault.frv = JSON header (plaintext) + encrypted payload
{
  "format": "orrery-vault",
  "version": 1,
  "kdf":    { "alg": "argon2id", "memKiB": 65536, "iterations": 3, "parallelism": 1, "salt": "<b64>" },
  "wrappedKey": { "alg": "xchacha20poly1305", "nonce": "<b64>", "ct": "<b64>" },
  "payload":    { "alg": "xchacha20poly1305", "nonce": "<b64>", "ct": "<b64>" }
}
```

- **Key hierarchy:** master password → Argon2id → key-encryption key (KEK). The KEK unwraps a random 256-bit data key (DEK), and the DEK encrypts the payload. Changing the master password only re-wraps the DEK.
- **Payload:** JSON array of `Credential` objects.
- **Associated data:** the header bytes (minus the ciphertexts) are bound as AEAD associated data, so tampering with KDF parameters is detected.
- **Library:** libsodium (via NSec or Sodium.Core) for both Argon2id and XChaCha20-Poly1305. It behaves the same on all three OSes. .NET's own `AesGcm` depends on the platform's crypto library, and .NET has no built-in Argon2. The cost is a native dependency shipped per platform, which we already accept for IronRDP.
- KDF parameters are stored, not hard-coded, so they can be raised later. The vault re-encrypts with current parameters on the next successful unlock.

### 3.4 Unlock experience
- The vault unlocks on first use: when a connection needs a secret, or when the Credentials section opens. The app starts without asking.
- **Auto-lock** after an idle period (default 30 min; configurable; "never" allowed). Live sessions keep running when the vault locks.
- **"Remember on this device"** (installed mode only): store the DEK in the OS keychain so the vault opens without the password on this machine. Offered in portable mode only with a clear warning, because it leaves a key on the host.
- Five failed attempts trigger an increasing delay. There is no lockout or wipe.
- There is **no recovery** if the master password is forgotten. Say so plainly when the vault is created, and offer an encrypted export.

### 3.5 Secrets in memory
- Hold secrets in byte arrays and zero them after use where we control the lifetime.
- Be honest about the limits: IronRDP's and SSH.NET's APIs take `string` passwords, and .NET strings can't be wiped. Keep secret strings short-lived, never log them, and never include them in crash reports (scrub the `Credential` type in logging).

### 3.6 SSH keys
- Keys can be **stored in the vault** (best for portable mode) or **referenced by path**. In portable mode, paths are stored relative to the data folder when they point inside it.
- ssh-agent support (Pageant / OpenSSH agent on Windows, `SSH_AUTH_SOCK` elsewhere) comes later. The credential model already allows a `kind` for it.

---

## 4. mRemoteNG import

### 4.1 Entry points
- **First run:** if `%APPDATA%\mRemoteNG\confCons.xml` exists (Windows), offer "Import your mRemoteNG connections?".
- **Drag and drop** a `.xml` onto the connection tree (the drop zone shown in the mockup).
- **File › Import › mRemoteNG…** for any path, including a portable mRemoteNG folder or a `confCons.xml.backup`.

The source file is opened **read-only** and never modified.

### 4.2 Pipeline

```
read file
  → parse root attributes (encryption settings, ConfVersion)
  → verify password (default key, else prompt for master password)
  → decrypt whole file if FullFileEncryption="true"
  → walk <Node> tree → intermediate model (all attributes kept as strings)
  → map to Orrery model (§4.5)
  → decrypt Password attributes → credentials (deduplicated, §4.6)
  → preview dialog (counts, warnings, target folder)
  → commit: connections.json + vault in one transaction (backup first)
  → write import report (§4.7)
```

The mapping stage is pure: file in, model plus warnings out, with no I/O. That keeps it unit-testable against fixtures.

### 4.3 File format (to verify against real files)

> Checked against a real ConfVersion 2.6 export (2026-10-29): the root attributes, the 113 per-node attributes and the `Protected` marker all match. An anonymised copy is the test fixture `tests/Orrery.Core.Tests/Import/Fixtures/mremoteng-2.6-no-passwords.xml`.

Root element:

```xml
<mrng:Connections xmlns:mrng="http://mremoteng.org" Name="Connections" Export="false"
    EncryptionEngine="AES" BlockCipherMode="GCM" KdfIterations="1000"
    FullFileEncryption="false" Protected="<encrypted marker>" ConfVersion="2.6">
  <Node Name="Production" Type="Container" Expanded="true" Id="<guid>" ...>
    <Node Name="web-01" Type="Connection" Hostname="web-01.corp.local" Protocol="RDP"
          Port="3389" Username="admin" Domain="corp" Password="<encrypted>"
          InheritUsername="false" InheritDomain="true" ... />
  </Node>
</mrng:Connections>
```

- Each `<Node>` carries every property as an attribute, plus a matching `Inherit<Property>` boolean.
- `Protected` holds an encrypted marker string. Decrypting it with the default password tells us whether the user set a custom master password.
- `ConfVersion` identifies the format generation. Older files use a different cipher setup (see §4.4).

### 4.4 Decryption
- **Current format (AES-GCM), confirmed:** each value is base64 of salt (16 bytes) + nonce (16 bytes) + ciphertext + 128-bit tag. The key is PBKDF2-HMAC-SHA1 of the password over the salt for `KdfIterations` rounds, 256 bits; the salt is also the GCM associated data. A real file's `Protected` marker decrypts to `ThisIsNotProtected` with the default key. .NET's `AesGcm` only takes 12-byte nonces, so this uses BouncyCastle.
- **Default password:** if no master password is set, mRemoteNG uses a built-in default. Try it first; prompt only if the `Protected` check fails.
- **Legacy format (not supported yet):** older `ConfVersion`s used an AES-CBC (Rijndael) scheme with a different key derivation. For now the importer asks the user to open and save the file once in a current mRemoteNG. Add a decryptor when a real legacy file is available to test against.
- **Full-file encryption:** when `FullFileEncryption="true"`, the root element's content is one encrypted blob. Decrypt it, then parse the inner XML.
- Wrong password: re-prompt. Three failures: offer to import **without passwords** (connections only, credentials left empty and flagged in the report).

### 4.5 Mapping

**Structure**
- `Type="Container"` → folder. `Type="Connection"` → connection.
- `Id` is kept as `importSourceId` so a re-import updates matching items instead of duplicating them (§4.6).
- `Expanded`, `Descr` (→ notes) and `Icon` (→ nearest built-in icon, else default) carry over.

**Inheritance:** Orrery keeps mRemoteNG's per-property inheritance, so `Inherit*="true"` maps directly onto "inherit from parent". The tree's meaning stays the same; nothing is flattened. Inherit flags for properties we don't model are dropped and counted in the report.

**Protocols**

| mRemoteNG `Protocol` | Orrery | Notes |
|---|---|---|
| `RDP` | RDP | |
| `SSH2` | SSH | |
| `SSH1` | SSH, flagged | SSH-1 is obsolete and unsupported by SSH.NET; imported but marked so it can't silently fail |
| `VNC` | VNC | |
| `Telnet` | Telnet | |
| `Rlogin`, `RAW` | Skipped, listed in report | Revisit if users ask |
| `HTTP`, `HTTPS` | Web link (opens in the default browser) | No embedded browser in v1 |
| `ICA` | Skipped, listed | Citrix |
| `PowerShell` | Skipped, listed | Candidate for later (PowerShell remoting in a terminal tab) |
| `IntApp` | Skipped, listed | External tools are out of scope for v1 |

**Common fields:** `Name`, `Hostname`, `Port`, `Username`, `Domain`, `Password` (→ credential), `Descr`.

**RDP fields (examples; complete the table from fixtures):** `Resolution`, `Colors`, `UseCredSsp`, `RDPAuthenticationLevel`, `RedirectClipboard`, `RedirectDiskDrives`, `RedirectPrinters`, `RedirectSound`, `RedirectKeys`, `DisplayWallpaper`, `DisplayThemes`, `RDGatewayUsageMethod` / `RDGatewayHostname` / gateway credential fields, `LoadBalanceInfo`, `ConnectToConsole`.
Any RDP setting IronRDP can't honour yet (gateway is the likely one) is **still imported** and flagged "not supported yet", so nothing is lost when support arrives.

**SSH:** `PuttySession` refers to a saved PuTTY session in the Windows registry. v1 records the name and flags it. A later option reads `HKCU\Software\SimonTatham\PuTTY\Sessions\<name>` on Windows to pull in key paths and ports.

### 4.6 Credentials and deduplication
- Each connection with a non-inherited `Username`/`Domain`/`Password` produces a credential candidate.
- Identical (username, domain, password) triples merge into **one** credential, named `user@domain` with a numeric suffix if needed. This usually turns hundreds of copied passwords into a handful of shared credentials.
- Where mRemoteNG inherited credentials from a folder, the credential is attached to that folder and inherited. The structure is the same as before.
- Secrets are written to whichever store is active (§3.2). If the vault is locked, unlock it before commit, not halfway through.
- **Re-import:** items matched by `importSourceId` are updated. Items the user has edited since the last import are listed in the preview as conflicts (keep mine / take theirs). New items are added, and nothing is deleted automatically.

### 4.7 Preview and report
The **preview** shows folder, connection and credential counts, protocol breakdown, warnings, and the destination: a new top-level folder named `Imported from mRemoteNG (<date>)` by default, or merged into the root.

The **report** is saved to `<data>/import/<timestamp>.md` and linked from the success toast:
- Skipped items with reasons (unsupported protocol, parse error)
- Fields dropped or not supported yet, with counts
- Credentials merged
- Items imported without passwords

The report never contains secrets.

### 4.8 Out of scope for v1 (tracked)
- mRemoteNG's SQL Server connection backend
- `extApps.xml` (external tools)
- PuTTY registry sessions beyond recording the name
- Exporting back to `confCons.xml`

### 4.9 Testing
- **Fixtures:** generate `confCons.xml` files from real mRemoteNG builds (at least the current stable and the current nightly, plus one legacy-format version), each with a known master password and with the default password. Cover every protocol, nested inheritance, full-file encryption, Unicode names and an empty file. **Never commit real user files.**
- **Golden tests:** fixture in → expected Orrery model (JSON) out, including the warning list.
- **Round trip:** import → re-import the same file → no changes. Import → edit → re-import → conflict reported.
- **Security:** parse XML with DTD processing prohibited and no XML resolver (blocks XXE and entity-expansion attacks). Cap the file size, and fuzz the parser with malformed input.

### 4.10 Licensing note
mRemoteNG is GPL-2.0. Implementing compatibility with its file format is fine. **Do not copy its code**, including the crypto and serializer classes. Write the importer from the format as observed in fixtures and documented behaviour, so Orrery can stay MIT-licensed.

---

## 5. Open questions
1. ~~**License for Orrery:**~~ Decided: MIT (see `LICENSE`). The clean-room rule in §4.10 still applies.
2. **Vault in installed mode:** OS keychain by default (seamless) or vault file by default (one encrypted file to back up)? This draft proposes the keychain.
3. **Portable on macOS:** worth supporting beyond "it works"? Gatekeeper and App Translocation make running from removable media awkward.
4. **Shared data folder:** if someone points two machines at one synced custom folder, the lock file covers simultaneous use but not sync conflicts. Is a warning enough for v1?
5. **Credential types:** add a "prompt every time" credential (no stored secret) in v1? mRemoteNG users with blank passwords rely on this behaviour.
