# Orrery

Orrery is a remote connection manager for Windows, Linux and macOS. It keeps your RDP, SSH, VNC and Telnet
connections in one tree, opens them in tabs, and stores their passwords in the system's own secret store. It is built on
Avalonia and .NET 10, inspired by [mRemoteNG](https://mremoteng.org/), and imports mRemoteNG's `confCons.xml`.
Orrery isn't affiliated with the mRemoteNG project.

Version 0.1.0. Early days: the protocols work, but several things still need testing on real machines (see
[Status](#status)).

## Features

- **Protocols**: RDP (through [IronRDP](https://github.com/Devolutions/IronRDP)), SSH (SSH.NET), VNC
  (MarcusW.VncClient) and Telnet.
- **Jump hosts**: reach a machine through one or more saved SSH connections (like `ssh -J`). SSH goes through them
  directly, and RDP, VNC and Telnet through an SSH tunnel Orrery opens and closes with the session. SSH connections
  also take port forwards (`L`, `R`, `D`).
- **SFTP file browser**: open an SSH connection's files in a tab beside its terminal (right-click it, or its tab,
  and choose *Browse files*). Browse, upload (or drop files in), download, rename, delete and make folders, with
  progress and Cancel. It uses the same login, agent keys and jump hosts as the terminal.
- **Display quality**: per connection or folder, Balanced, Best look or Fastest. For RDP it picks the remote
  desktop's visual effects (wallpaper, themes, font smoothing, desktop composition); for VNC, the JPEG quality the
  server uses when it sends Tight (which needs TurboJPEG installed). HDR isn't possible over RDP or VNC, and RDP
  can't follow your display scaling yet because IronRDP's .NET binding doesn't pass it on; *Enlarge to match
  display scaling* enlarges the picture locally instead.
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
| Linux | `~/.config/orrery` (XDG directories are respected), for the .deb, .rpm and AppImage |

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

To build everything this machine can, run:

```powershell
./build/Build-All.ps1
```

It runs the three scripts below in turn. If a tool is missing, that step is skipped with a warning instead of stopping
the rest, and a summary at the end lists what was built and what wasn't. On Windows you get everything except the
AppImage. Pass `-NfpmPath` or `-IsccPath` for tools that aren't on PATH, and `-SkipPortable`, `-SkipInstaller` or
`-SkipLinux` to leave a step out.

| Script | Builds | Needs |
| --- | --- | --- |
| `./build/package.ps1` | Portable zips for win-x64, win-arm64, linux-x64, linux-arm64, osx-arm64 and osx-x64 (`-Runtimes win-x64` for just one) | .NET SDK |
| `build\Build-Installer.ps1` | Windows installer | Windows, [Inno Setup](https://jrsoftware.org/isinfo.php) 6 or 7 |
| `./build/Build-LinuxPackages.ps1` | .deb and .rpm, x64 and arm64 | [nfpm](https://nfpm.goreleaser.com/install/) (runs on Windows too) |
| | AppImage, x64 and arm64 | Linux, [appimagetool](https://github.com/AppImage/appimagetool/releases) |

Packages are self-contained, so the target machine needs no .NET runtime. Output goes to `artifacts/`. On Windows and
Linux the portable app is a single executable with its native libraries beside it; on macOS it is an `Orrery.app`
bundle, not yet signed or notarised. On Linux, run `chmod +x Orrery` after unzipping a portable zip, and the machine
needs X11 (or XWayland), fontconfig and ICU; the .deb and .rpm declare these as dependencies. There's no Flatpak.
[docs/packaging.md](docs/packaging.md) has the details, including Linux requirements, third-party notices and lock files.

## Repository layout

| Path | What it holds |
| --- | --- |
| `src/Orrery.App` | The Avalonia app: windows, connection tree, properties pane, settings |
| `src/Orrery.Core` | Model, storage, credentials, vault, import and export |
| `src/Orrery.Protocols.*` | RDP, SSH, VNC and Telnet sessions |
| `tests/` | xUnit tests, including headless Avalonia UI tests |
| `build/` | Packaging, installer and third-party notice scripts |
| `installer/` | Inno Setup script for the Windows installer; `installer/linux/` holds the Linux launcher and icons |
| `docs/` | Design notes and packaging guide |

## Status

Tested on Windows, including RDP resizing and clipboard and the installer. Still to be checked on real machines:
Linux and macOS builds (including the .deb, .rpm and AppImage), ARM builds, and VNC authentication. The Windows
executables aren't code-signed, so SmartScreen may warn on first launch and antivirus tools can flag the single-file
executable as a false positive.

## Licence and credits

Orrery is MIT-licensed, © 2026 InspireNXE. See [LICENSE](LICENSE).

It's built on open-source work, all under permissive licences (MIT, Apache 2.0, BSD, ISC, OFL):

- [Avalonia](https://avaloniaui.net/) (UI), [SkiaSharp](https://github.com/mono/SkiaSharp) and
  [HarfBuzzSharp](https://github.com/mono/SkiaSharp) (drawing and text), and the [Inter](https://rsms.me/inter/) font
- [IronRDP](https://github.com/Devolutions/IronRDP) by Devolutions (RDP), [SSH.NET](https://github.com/sshnet/SSH.NET)
  (SSH), [MarcusW.VncClient](https://github.com/MarcusWichelmann/MarcusW.VncClient) (VNC, through the [community fork](https://github.com/karbonbaron/MarcusW.VncClient))
- [NSec](https://nsec.rocks/) and [libsodium](https://libsodium.org/) (vault and export encryption),
  [BouncyCastle](https://www.bouncycastle.org/) (mRemoteNG file encryption)
- [.NET](https://dotnet.microsoft.com/) by Microsoft and the .NET Foundation, which ships inside every package
- Portions of this software are copyright © The FreeType Project (https://freetype.org). All rights reserved.
  (FreeType is built into Skia.)

Every package carries `THIRD-PARTY-NOTICES.txt`, generated from what that build actually ships, with each component's
licence and copyright notices. The AppImage also carries `APPIMAGE-RUNTIME-NOTICES.txt` for the AppImage runtime and
the libraries linked into it, including libfuse (LGPL 2.1). mRemoteNG is GPL-licensed: Orrery reads and writes its
`confCons.xml` format, written from the file format, and contains none of mRemoteNG's code.
