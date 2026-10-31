# Orrery

Orrery is a remote connection manager for Windows, Linux and macOS. It keeps your RDP, SSH, VNC and Telnet
connections in one tree, opens them in tabs, and stores their passwords in the system's own secret store. It is a
rebuild of [mRemoteNG](https://mremoteng.org/) on Avalonia and .NET 10, and imports mRemoteNG's `confCons.xml`.

Version 0.1.0. Early days: the protocols work, but several things still need testing on real machines (see
[Status](#status)).

## Features

- **Protocols**: RDP (through [IronRDP](https://github.com/Devolutions/IronRDP)), SSH (SSH.NET), VNC
  (MarcusW.VncClient) and Telnet.
- **Connection tree**: folders, drag and drop, multi-select (Ctrl- and Shift-click), filter, inherited settings,
  and host groups (a host with its guest machines one level below it).
- **Credentials**: kept in Windows Credential Manager, the macOS Keychain or the Linux Secret Service, or in an
  encrypted vault file (Argon2id, XChaCha20-Poly1305) when there is no system store, in portable mode, or when you
  choose it.
- **Import**: mRemoteNG `confCons.xml` and Orrery `.json`.
- **Export**: Orrery `.json`, mRemoteNG `confCons.xml` and CSV. Passwords and keys can go into the `.json` and
  `confCons.xml` files, encrypted under a password you set; CSV never carries them.
- **Installed or portable**: the installed copy keeps data in your profile; the portable copy keeps it wherever you
  choose on first launch.
- Dark-first interface.

## Keyboard shortcuts

On macOS, Cmd replaces Ctrl.

| Keys | Action |
| --- | --- |
| Ctrl+K | Quick connect |
| Ctrl+, | Settings |
| Ctrl+B | Show or hide the side panel |
| Ctrl+Alt+B | Show or hide the properties pane |
| Ctrl+Alt+Enter (Ctrl+Cmd+F on macOS) | Full screen (can be changed in settings) |
| Enter | Connect the selected connections |
| F2 | Rename |
| Ctrl+D | Duplicate |
| Delete | Delete the selected rows |

A focused remote session receives keys first, so these go to the remote machine while it has focus.

## Where data lives

| | Installed |
| --- | --- |
| Windows | `%APPDATA%\Orrery` (cache and logs under `%LOCALAPPDATA%\Orrery`) |
| macOS | `~/Library/Application Support/Orrery` |
| Linux | `~/.config/orrery` (XDG directories are respected) |

A portable copy has an `orrery.portable.json` file beside the executable and asks on first launch where to keep its
data.

## Building

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download). From the repo root:

```powershell
dotnet build Orrery.sln
dotnet test Orrery.sln
dotnet run --project src/Orrery.App
```

### Packages

```powershell
./build/package.ps1                      # portable zips for win-x64, win-arm64, linux-x64, linux-arm64, osx-arm64, osx-x64
./build/package.ps1 -Runtimes win-x64    # just one
build\Build-Installer.ps1                # Windows installer (needs Inno Setup 6 or 7)
```

Packages are self-contained, so the target machine needs no .NET runtime. Output goes to `artifacts/`. On Windows and
Linux the portable app is a single executable; on macOS it is an `Orrery.app` bundle, not yet signed or notarised.
[docs/packaging.md](docs/packaging.md) has the details, including third-party notices and lock files.

## Repository layout

| Path | What it holds |
| --- | --- |
| `src/Orrery.App` | The Avalonia app: windows, connection tree, properties pane, settings |
| `src/Orrery.Core` | Model, storage, credentials, vault, import and export |
| `src/Orrery.Protocols.*` | RDP, SSH, VNC and Telnet sessions |
| `tests/` | xUnit tests, including headless Avalonia UI tests |
| `build/` | Packaging, installer and third-party notice scripts |
| `installer/` | Inno Setup script for the Windows installer |
| `docs/` | Design notes and packaging guide |

## Status

Tested on Windows, including RDP resizing and clipboard and the installer. Still to be checked on real machines:
Linux and macOS builds, ARM builds, and VNC authentication. The Windows executables aren't code-signed, so SmartScreen may warn
on first launch and antivirus tools can flag the single-file executable as a false positive.

## Licence

MIT, © 2026 InspireNXE. See [LICENSE](LICENSE). Third-party licences ship in `THIRD-PARTY-NOTICES.txt` with each
package.
