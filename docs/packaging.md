# Packaging Orrery

Three kinds of package (the Windows installer, portable zips, and Linux .deb, .rpm and AppImage), all
self-contained: the .NET runtime ships beside the app, so nothing else needs installing.

`build/Build-All.ps1` runs all three builds below (portable zips, Windows installer, Linux packages). It skips a
step whose tool is missing, with a warning, and prints what was built and what was skipped.

## Orrery Setup (Windows installer)

Built with Inno Setup, the same way as Prober's desktop setup (`installer\Orrery.iss` is modelled on
`Prober.Installer\Prober.Desktop.iss`, and `installer\WizardLayout.iss` is copied from Prober so the wizard looks
the same). You need PowerShell 5.1 or later, the .NET SDK, and Inno Setup 7 installed per user
(`%LOCALAPPDATA%\Programs\Inno Setup 7\ISCC.exe`) or for all users under either Program Files folder; pass
`-IsccPath` for anything else. From the repo root:

```powershell
build\Build-Installer.ps1
```

It publishes Orrery for win-x64 into `artifacts\installer-publish` (emptied first), generates
`THIRD-PARTY-NOTICES.txt` there, and compiles `artifacts\installer\Orrery-Setup-<version>.exe`. The version comes
from `<Version>` in `Directory.Build.props`. `-PublishDir <path> -SkipPublish` reuses an existing publish while
you iterate on the `.iss`.

What the setup does: installs for everyone under `Program Files\Orrery` (administrator rights), adds a Start menu
entry and, if ticked, a desktop shortcut, and offers to start Orrery at the end. The installed copy runs in
installed mode, so each person's connections, settings and credentials stay in their own profile
(`%APPDATA%\Orrery`) and survive upgrades and uninstalling. Setup removes any stray `orrery.portable.json` in the
install folder, which would otherwise switch it to portable mode.

## Portable packages (Windows, Linux, macOS)

```powershell
./build/package.ps1                      # win-x64, win-arm64, linux-x64, linux-arm64, osx-arm64, osx-x64
./build/package.ps1 -Runtimes win-x64    # just one
```

Each writes `artifacts/Orrery-<version>-<rid>-portable.zip` with the app, `LICENSE`, `THIRD-PARTY-NOTICES.txt` and
an empty `orrery.portable.json`, so the first launch asks where to keep the data
([design doc](design-storage-vault-import.md) §2.2). On Windows and Linux the app is one executable (`Orrery.exe`)
holding the managed code, with its native libraries (Skia, HarfBuzz, ANGLE, IronRDP, libsodium) beside it. The
bundle is neither compressed nor self-extracting, so nothing is unpacked at launch: Windows Defender's cloud
heuristics flagged the compressed, self-extracting build as `Trojan:Script/Wacatac.B!ml` when it was downloaded in
Edge. Unsigned downloads can still be flagged until the executables are code-signed. The single-file analyzer is
turned off for that publish, because it would add a package that isn't in the lock files. On macOS the zip holds `Orrery.app` (Info.plist and icon),
with the marker beside the bundle. The bundle isn't signed or notarised yet, so macOS asks before the first launch.
Zip files carry no Unix permissions from Windows: on Linux and macOS, `chmod +x` the executable after unzipping, or
build those packages on Linux or macOS.

## Linux packages (.deb, .rpm, AppImage)

```powershell
./build/Build-LinuxPackages.ps1                                       # every format, x64 and arm64
./build/Build-LinuxPackages.ps1 -Runtimes linux-x64 -Formats deb,rpm   # just x64 .deb and .rpm
```

Writes to `artifacts/linux/`: `orrery_<version>-1_amd64.deb`, `orrery_<version>-1_arm64.deb`,
`orrery-<version>-1.x86_64.rpm`, `orrery-<version>-1.aarch64.rpm`, and `Orrery-<version>-x86_64.AppImage` and
`-aarch64.AppImage`. Each uses an ordinary multi-file publish, not the single executable, so nothing unpacks to
`~/.net` on first launch. Every format runs in installed mode, with data in `~/.config/orrery`.

- **.deb and .rpm** are built with [nfpm](https://nfpm.goreleaser.com/install/), which runs on Windows, Linux and
  macOS and sets Unix file modes itself (no `chmod` problem). Put `nfpm` on PATH or pass `-NfpmPath`. The app goes to
  `/opt/orrery`, with `/usr/bin/orrery` linked to it, the launcher in `/usr/share/applications/orrery.desktop`, icons
  in the hicolor theme, and `LICENSE` and `THIRD-PARTY-NOTICES.txt` in `/usr/share/doc/orrery`. The packages
  require the C runtime, ICU, OpenSSL, fontconfig and the X11 libraries, and recommend libsecret. Their dependency
  lists are in the script.
- **AppImage** needs [appimagetool](https://github.com/AppImage/appimagetool/releases) (on PATH or
  `-AppImageToolPath`), which only runs on Linux, so on Windows the script skips it with a warning. The image is
  read-only and has no `orrery.portable.json`, so it runs in installed mode; settings' restart relaunches the
  `.AppImage` file (`$APPIMAGE`), not the copy inside its mount.

The launcher, the AppImage's `AppRun` and the icons are in `installer/linux/`. So is
`APPIMAGE-RUNTIME-NOTICES.txt`, which the AppImage carries in `usr/share/doc/orrery`. It covers the runtime that
appimagetool puts in front of the image, which is statically linked with libfuse 3 (LGPL 2.1), squashfuse, zstd,
zlib, mimalloc and musl. Update it if a newer runtime links something else (see `src/runtime/Makefile` in
AppImage/type2-runtime). `build/generate-art.py` draws the icons.
The window's X11 class is set to `Orrery` (`Program.cs`) to match the launcher's `StartupWMClass`, so docks group the
window with its icon.

Status: the .deb and .rpm build on Windows, and their contents have been checked (file modes, the symlink, control fields
and dependencies). None of them has been installed on a real Linux machine yet, and the AppImage hasn't been built,
since that needs Linux.

## Running on Linux

None of the Linux builds has been run on a real machine yet. The .deb and .rpm pull in what they need. For the
portable zip and the AppImage, the machine needs:

- **An X11 session.** Avalonia draws through X11; on Wayland it runs under XWayland.
- **fontconfig and ICU** (`libfontconfig1` and `libicu` on Debian and Ubuntu, `fontconfig` and `libicu` on Fedora).
  Globalization isn't set to invariant, so .NET won't start without ICU. Most desktop installs already have both.
- **libsecret and a Secret Service** (GNOME Keyring or KWallet) to keep credentials in the system keyring. Without
  them, or in portable mode, credentials go to the encrypted vault file.

After unzipping the portable zip, `chmod +x Orrery` (zips made on Windows carry no Unix permissions).

## IronRDP native libraries

Devolutions.IronRdp adds its native library for every Windows (or, on a Linux machine, Linux) runtime under
`runtimes/<rid>/native/`, chosen by the machine doing the build, not the target. A publish for one runtime already
puts that runtime's library beside the app, so `Orrery.App.csproj` drops those copies from every runtime-specific
publish (`DropUnusedIronRdpNatives`). That saves about 16 MB in Linux and macOS packages built on Windows, and one
unused DLL in Windows packages. Builds without a runtime, like `dotnet run`, keep them.

## Third-party notices

`build/Generate-ThirdPartyNotices.ps1 -PublishDir <publish output>` writes `THIRD-PARTY-NOTICES.txt` from the
publish's `deps.json`, so it lists exactly what that platform ships, including the .NET runtime pack. Licence and
notice files are copied unchanged from each package; a package that only names its licence (MIT) gets the
standard text with its copyright line. `build/licenses/<package id>.txt` adds texts a package lacks or bundles
separately (IronRDP's MIT licence, the Inter font's OFL), with their source named. The script fails if a shipped
package has no licence text it can find, so a new dependency can't ship unacknowledged.

## Package versions and lock files

Every project keeps a `packages.lock.json` beside its `.csproj`, recording the exact version and content hash of each
package it restores, transitive ones and the self-contained runtime packs for every platform in
`<RuntimeIdentifiers>` in `Directory.Build.props` included. `nuget.config` clears the machine's package sources and uses
nuget.org, with every package mapped to it except `Devolutions.IronRdp`, which comes from a locally built package for
now (see the README's Building section). Packages built this way need that folder on the building machine, and their
Linux and macOS IronRDP libraries don't have the RDP clipboard yet.

Changing a package (adding, removing or bumping one, including the `AvaloniaVersion`, `IronRdpVersion` and
`BouncyCastleVersion` properties in `Directory.Build.props`) needs this, with the updated `packages.lock.json` files
in the same commit:

```powershell
dotnet restore Orrery.sln --force-evaluate
```

Ordinary restores update the lock files as needed. Builds with `-p:OrreryLockedRestore=true` (the packaging scripts
pass it) or `ContinuousIntegrationBuild=true` restore in locked mode and fail with NU1004 if the packages no longer
match the lock files. To check by hand: `dotnet restore Orrery.sln --locked-mode`.

## Icons and wizard art

`python build/generate-art.py` (needs Pillow) redraws `src/Orrery.App/Assets/orrery.{png,ico,icns}` and
`installer/media/WizardImage.png` from the logo in `MainWindow.axaml`.
