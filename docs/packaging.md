# Packaging Orrery

Three kinds of package (the Windows installer, portable zips, and Linux .deb, .rpm and AppImage), all
self-contained: the .NET runtime ships beside the app, so nothing else needs installing.

`build/Build-All.ps1` runs all three builds below (portable zips, Windows installer, Linux packages). It skips a
step whose tool is missing, with a warning, and prints what was built and what was skipped.

## Orrery Setup (Windows installer)

Built with Inno Setup, the same way as Prober's desktop setup (`installer\Orrery.iss` is modeled on
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

Each writes `artifacts/Orrery-<version>-<rid>-portable.zip`, with `macos` in place of .NET's `osx` (so
`Orrery-<version>-macos-arm64-portable.zip`), with the app, `LICENSE`, `THIRD-PARTY-NOTICES.txt` and
an empty `orrery.portable.json`, so the first launch asks where to keep the data
([design doc](design-storage-vault-import.md) §2.2). On Windows and Linux the app is one executable (`Orrery.exe`)
holding everything. It's compressed, and on first launch it unpacks its native libraries (Skia, HarfBuzz, ANGLE,
IronRDP, libsodium) to a cache folder, since those can't load from inside it. Windows Defender's cloud heuristics can
flag an unsigned download as `Trojan:Script/Wacatac.B!ml` in Edge; shipping the native libraries beside the
executable, uncompressed, made no difference, so only code-signing is likely to stop it. The single-file analyzer is
turned off for that publish, because it would add a package that isn't in the lock files. On macOS the zip holds `Orrery.app` (Info.plist and icon),
with the marker beside the bundle. The bundle isn't signed or notarized yet, so macOS asks before the first launch.
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
  `-AppImageToolPath`), which only runs on Linux, so on Windows the script skips it with a warning. appimagetool
  downloads the newest AppImage runtime for each image unless `-AppImageRuntimeDir` names a folder holding
  `runtime-x86_64` and `runtime-aarch64` from [type2-runtime](https://github.com/AppImage/type2-runtime/releases). The image is
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

## GitHub Actions

`.github/workflows/packages.yml` builds every package above on each push (any branch, and `v*` tags), each pull
request, and by hand (`workflow_dispatch`). A newer push to the same branch or pull request cancels the older run; tag
builds always finish, and so do runs that publish the IronRDP package. Apart from that package, it publishes nothing:
the packages are the run's artifacts (`orrery-windows-portable`, `orrery-windows-installer`, `orrery-linux-portable`,
`orrery-linux-packages`, `orrery-macos-portable`).

- **IronRDP first.** Until a `Devolutions.IronRdp` for this `IronRdpVersion` is published (below), the workflow builds
  it itself, the way IronRDP's own `nuget-publish.yml` does, for the six platforms Orrery ships (the package leaves out
  Android, iOS and the macOS universal library, which Orrery doesn't use). The fork and commit are `IRONRDP_REPOSITORY`
  and `IRONRDP_SHA` in the workflow's `env`, the one place to change them. **That commit must be pushed to the fork
  first**, or the IronRDP jobs fail at checkout. The package version is `IronRdpVersion` from `Directory.Build.props`.
- **The published package.** When `build/ironrdp-package.json` has a `sha256` and its `version` is `IronRdpVersion`,
  the build jobs download that package with `build/Get-IronRdp.ps1` (from its `url`, or from this repository's release
  `ironrdp-<version>` when `url` is empty) and skip the IronRDP jobs, unless the run is publishing. A json still
  describing an older version is ignored with a warning, so the workflow keeps building until the new one is
  published.
- **Lock files.** With the published package, the lock files must already record it: each build job runs
  `dotnet restore Orrery.sln --locked-mode`, which fails with NU1403 if they don't. With the workflow's own build, the
  content hash differs from the one in the lock files, so each build job points `ironrdp-local` at that package and
  runs `build/Update-IronRdpLockHashes.ps1`. It runs `dotnet restore Orrery.sln --force-evaluate` and fails if any lock
  file changed beyond `Devolutions.IronRdp`'s content hash, so everything else stays exactly as committed.
- **Jobs.** Windows runs the tests, then builds the win-x64 and win-arm64 zips and Orrery Setup (Inno Setup 7.1.1).
  Linux builds the linux-x64 and linux-arm64 zips, .deb, .rpm and both AppImages (nfpm 2.48.0, appimagetool 1.9.1
  and the type2-runtime 20251108 runtimes, passed with `-AppImageRuntimeDir` so appimagetool doesn't download the
  newest one). macOS builds the macos-arm64 and macos-x64 zips.
- **Pinned.** Every action is pinned by commit, every downloaded tool by version and SHA-256 (the workflow says where
  each hash came from), the Chocolatey packages by version, Rust by IronRDP's `rust-toolchain.toml`, and the .NET SDK
  by `DOTNET_SDK_VERSION` (written to a `global.json` on the runner only, so local builds can use any .NET 10 SDK).
- **Caches.** The built IronRDP package is cached as `ironrdp-nupkg-<IRONRDP_CACHE_EPOCH>-<IRONRDP_SHA>-<IronRdpVersion>`;
  when that key exists, the IronRDP native builds are skipped. Bump `IRONRDP_CACHE_EPOCH` after changing the IronRDP
  jobs to force a rebuild. Cargo's registry and `target` folder are cached per runner and platform as
  `cargo-<runner>-<os>-<arch>-<IRONRDP_SHA>`, with a new commit starting from the last one's. NuGet packages are cached
  per OS as `nuget-<OS>-<hash of the lock files>`, without `Devolutions.IronRdp`. GitHub only shares caches from the
  default branch with other branches, so a branch's first run builds IronRDP unless `main` already has it cached.
- **Permissions.** The workflow's token can only read the repository, except in `publish-ironrdp`, the one job that
  creates a release.

## A new IronRDP package

`Devolutions.IronRdp` comes from a fork of IronRDP and isn't on nuget.org, so Orrery publishes each version it uses
as a release asset of its own repository: the release `ironrdp-<version>`, holding
`Devolutions.IronRdp.<version>.nupkg`. `build/ironrdp-package.json` names that version, the fork and commit it was
built from, the asset's URL and its SHA-256. `build/Get-IronRdp.ps1` downloads it into `.nuget-local` and refuses a
file whose SHA-256 differs, or a json with no hash. A published package is never replaced: everyone whose json names
it would get a mismatch. A change to the package always gets a new version.

1. Push the IronRDP commit to the fork. In `.github/workflows/packages.yml`, set `IRONRDP_SHA` to it (and
   `IRONRDP_REPOSITORY` if the fork changes). In `Directory.Build.props`, bump `IronRdpVersion`, for example from
   `2026.10.29-orrery.2` to `2026.10.29-orrery.3`.
2. Build that package locally (or take the workflow's `ironrdp-nupkg` artifact), put it in `.nuget-local` with
   `./build/Get-IronRdp.ps1 -FromFolder <folder> -Sha256 <its SHA-256>`, run `dotnet restore Orrery.sln --force-evaluate`
   and commit everything on `main`. Until the new version is published, the workflow builds the package itself and
   refreshes the lock files' IronRDP hash for its own build. A package built on one machine only has that machine's
   native library from the fork: the owner's Windows build of `2026.10.29-orrery.2` carries older Linux and macOS
   libraries without the RDP clipboard. The workflow builds all six, so the published package is always its build.
3. Run the workflow on `main` by hand (Actions, Packages, Run workflow) with `publish-ironrdp` ticked. Once every
   package has built and the tests have passed with the package it built, the `publish-ironrdp` job creates the
   release with the runner's `gh`, downloads the asset again to check its SHA-256, and prints the
   `build/ironrdp-package.json` to commit (also in the run's summary). It fails if the release or its tag already
   exists.
4. Commit that json. Then make the lock files record the published package, so every machine and the workflow share
   one package and one content hash:

   ```powershell
   ./build/Get-IronRdp.ps1
   dotnet restore Orrery.sln --force-evaluate
   ```

   If `Get-IronRdp.ps1` warns that NuGet's cache holds a different package of the same version, delete the folder it
   names first, or the restore keeps using the old one. Commit the changed `packages.lock.json` files with the json,
   or in the next commit. From then on the workflow uses the published package and restores against the lock files
   as committed.

The first package (`2026.10.29-orrery.2`) was built before any of this existed, so its lock files record the owner's
local build: after publishing it, step 4 applies to it too.

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
publish's `deps.json`, so it lists exactly what that platform ships, including the .NET runtime pack. License and
notice files are copied unchanged from each package; a package that only names its license (MIT) gets the
standard text with its copyright line. `build/licenses/<package id>.txt` adds texts a package lacks or bundles
separately (IronRDP's MIT license, the Inter font's OFL), with their source named. The script fails if a shipped
package has no license text it can find, so a new dependency can't ship unacknowledged.

## Package versions and lock files

Every project keeps a `packages.lock.json` beside its `.csproj`, recording the exact version and content hash of each
package it restores, transitive ones and the self-contained runtime packs for every platform in
`<RuntimeIdentifiers>` in `Directory.Build.props` included. `nuget.config` clears the machine's package sources and uses
nuget.org, with every package mapped to it except `Devolutions.IronRdp`, which comes from `.nuget-local`, filled by
`build/Get-IronRdp.ps1` (see the README's Building section and "A new IronRDP package" above).

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
