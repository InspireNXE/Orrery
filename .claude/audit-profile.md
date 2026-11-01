# Audit profile: Orrery

Orrery is a cross-platform remote connection manager (RDP, SSH, VNC, Telnet), rebuilt from mRemoteNG on
Avalonia. It's a desktop client: there's no server of ours, so the hostile parties are the remote hosts it
connects to, files it imports, other local users and processes, and anyone who gets hold of its data folder.

## Basics
- Rules file: none yet. `docs/design-storage-vault-import.md` is the closest thing to a standard for
  storage, the vault and import (unconfirmed: owner may want a CLAUDE.md written first).
- Base branch: `main` (no remote; never push)
- Output root: `docs/audit`
- Tooling output: `$LOCALAPPDATA/orrery-audit/<YYYY-MM>/tooling`; gitignore `docs/audit/*/tooling/` as a guard
- Stack: C# on .NET 10, Avalonia 11.3.22 (Fluent theme), xUnit with Avalonia.Headless. Protocols:
  Devolutions.IronRdp 2026.10.29 (Rust, native), SSH.NET 2026.0.0, Community.MarcusW.VncClient 2.0.8,
  own Telnet. Crypto: NSec.Cryptography 26.4.0 (libsodium), BouncyCastle.Cryptography 2.6.2.
- Build: `dotnet build Orrery.sln`
- Test: `dotnet test Orrery.sln`; focused: `dotnet test tests/<Project> --filter "FullyQualifiedName~<Name>"`
- Before building: stop Orrery processes the session started (`taskkill /IM Orrery.exe /F` locks the
  build output otherwise). The owner may have their own Orrery open; say so before killing one you
  didn't start.
- Shared checkout: no (one session at a time works in `W:\fluentRemote`)
- Commits: commit as you go on `main`, one commit per packet (owner's standing rule); never push

## Status
- Production: no. Version 0.1.0, unreleased. File formats (`connections.json`, `settings.json`,
  `vault.frv`, trust stores) can change without migration if a finding needs it (unconfirmed).
- Accepted risks (never report):
  - Telnet sends everything, passwords included, in clear text. That's the protocol; the app shows it.
  - Classic VNC authentication (DES challenge, 8-character passwords) and unencrypted VNC framebuffers are
    the protocol's weakness, not ours (unconfirmed).
  - Builds and installers aren't code-signed; the owner has no certificates.
  - A user who can already run code as the same OS user can read whatever that user can (keychain items
    while unlocked, process memory). Findings must cross a real boundary: another user, a remote host,
    an imported file, a stolen data folder, a locked vault, or a lower-integrity process of the same user
    (a sandboxed browser renderer, for example; owner decision 2026-10-30).

## Modules
| Module | Path | Notes |
|---|---|---|
| Core | `src/Orrery.Core` | Connections model, credential stores and vault, storage modes, mRemoteNG import, trust stores, terminal emulator |
| App | `src/Orrery.App` | Avalonia UI: main window, session views, prompts, settings, Windows key hook |
| RDP | `src/Orrery.Protocols.Rdp` | IronRDP wrapper: `RdpConnector`, `RdpSession` |
| SSH | `src/Orrery.Protocols.Ssh` | SSH.NET wrapper `SshSession`, own SSH agent client (Pageant, OpenSSH pipe, Unix socket) |
| Telnet | `src/Orrery.Protocols.Telnet` | Own Telnet client and option negotiation |
| VNC | `src/Orrery.Protocols.Vnc` | MarcusW.VncClient wrapper, clipboard |
| Packaging | `build/`, `installer/` | Inno Setup installer, portable packages, third-party notices |

## Trust boundaries

1. **RDP server to client.** TLS and certificate trust, CredSSP/NLA, frame data and sizes, server
   clipboard. `src/Orrery.Protocols.Rdp/RdpConnector.cs`, `RdpSession.cs`,
   `src/Orrery.Core/Security/CertificateTrust.cs`, `JsonCertificateTrustStore.cs`,
   `src/Orrery.App/Security/CertificatePromptWindow.cs`, `src/Orrery.App/Sessions/RemoteFrameView.cs`.
2. **SSH server to client.** Host key trust (first use, change), algorithm choice, login methods, key
   parsing. `src/Orrery.Protocols.Ssh/SshSession.cs`, `src/Orrery.Core/Security/HostKeyTrust.cs`,
   `src/Orrery.App/Security/HostKeyPromptWindow.cs`.
3. **SSH agent.** Our own client for Pageant, the OpenSSH named pipe and `SSH_AUTH_SOCK`: who we talk
   to, message parsing, signing requests. `src/Orrery.Protocols.Ssh/Agent/*`.
4. **VNC server to client.** Authentication, framebuffer sizes, server clipboard, local clipboard sent
   on focus. `src/Orrery.Protocols.Vnc/VncSession.cs`, `RemoteFrameView.cs`.
5. **Telnet server to client.** Option negotiation parser. `src/Orrery.Protocols.Telnet/TelnetSession.cs`.
6. **Terminal output from any host.** Escape sequences that reply to the host, set titles, change modes,
   report the mouse; paste into a session (bracketed paste); huge or malformed parameters.
   `src/Orrery.Core/Terminal/*`, `src/Orrery.App/Sessions/TerminalView.cs`.
7. **Imported files.** mRemoteNG `confCons.xml`: XML parsing, its password decryption, the import
   report written to disk. OpenSSH `~/.ssh/config`: Include following, size caps, ProxyJump into jump hosts,
   commands never run. `src/Orrery.Core/Import/*`, `src/Orrery.App/Import/ImportPreviewWindow.cs`,
   `MainWindow.axaml.cs` (`ImportAsync`, `WriteImportReport`).
8. **Secrets at rest and in memory.** Vault file format, key derivation, encryption, auto-lock, master
   password change; OS keychain backends; secrets in logs, exceptions, UI, clipboard and the import
   report. `src/Orrery.Core/Credentials/*`, `src/Orrery.App/Credentials/*`,
   `src/Orrery.App/Connections/ConnectPromptWindow.cs`.
9. **Data folder and local files.** Storage mode resolution, `ORRERY_HOME`, portable marker, installed
   pointer, Move data and deleting the old copy, file permissions on Linux and macOS, atomic writes,
   the folder lock, `--moved-from` on restart. `src/Orrery.Core/Storage/*`, `src/Orrery.App/App.axaml.cs`,
   `src/Orrery.App/Settings/SettingsView.cs`, `src/Orrery.App/Storage/PortableSetupWindow.cs`.
10. **OS integration.** Low-level Windows keyboard hook, process launches (`Process.Start`), save-file
    pickers (screenshots, session output). `src/Orrery.App/Input/WindowsKeyHook.cs`,
    `src/Orrery.App/App.axaml.cs`, `MainWindow.axaml.cs`.
11. **Dependencies.** Known advisories for every package and transitive package, how the protocol
    libraries are configured (TLS validation hooks, allowed SSH algorithms, strict key exchange), and
    native binaries shipped in the package. `Directory.Build.props`, every `*.csproj`.

## Never delete or weaken
- The trust prompts and their validators: `ServerCertificateValidatorTests`, `HostKeyValidatorTests`,
  and the code they cover. A failed or changed key or certificate must never connect silently.
- Vault crypto tests: `VaultFileStoreTests` (wrong password, tampering, master password change).
- `MRemoteNgCryptoTests` and `MRemoteNgImporterTests`.
- `DataMoverTests` and `DataLocationResolverTests`: they guard against moving or deleting the wrong folder.

## Tooling
- Vulnerable packages: `dotnet list Orrery.sln package --vulnerable --include-transitive`
- Outdated and deprecated: `dotnet list Orrery.sln package --outdated --include-transitive`,
  `dotnet list Orrery.sln package --deprecated`
- Upstream advisories that NuGet doesn't carry: GitHub security advisories and recent release notes for
  Devolutions/IronRDP (RustSec covers its crates), sshnet/SSH.NET, MarcusWichelmann/MarcusW.VncClient,
  NSec and libsodium, BouncyCastle, Avalonia. Web lookup at baseline.
- Secrets in history: `gitleaks detect --source .` (installed through winget; not on the bash PATH)
- Static analysis, clone detection and mutation testing aren't part of a security-only round.

## Docs
- Docs folder: `docs/`
- Voice reference: `docs/design-storage-vault-import.md`
- Existing docs to keep in sync: `docs/design-storage-vault-import.md`, `docs/packaging.md`

## Previous rounds
None.
