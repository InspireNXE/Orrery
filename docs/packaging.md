# Packaging Orrery

Three kinds of package (the Windows installer, portable zips, and Linux .deb, .rpm and AppImage), all
self-contained: the .NET runtime ships beside the app, so nothing else needs installing.

`build/Build-All.ps1` runs all three builds below (portable zips, Windows installer, Linux packages). It skips a
step whose tool is missing, with a warning, and prints what was built and what was skipped.

`build/package.ps1`, `build/Build-Installer.ps1` and `build/Build-LinuxPackages.ps1` build `<Version>` from
`Directory.Build.props` unless given `-Version 0.1.1` (say): the app then reports that version and the files are
named for it.

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

Orrery updates an installed copy by running the new setup with
`/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RELAUNCH=1`. `/RELAUNCH=1` makes setup start Orrery
again once it has installed (with `--restarted`, so it waits for the old one to let go of the data folder). Setup
starts it as the user who started setup, not as administrator, which only works when setup was started without
elevation and asked for it itself: an elevated setup has no other user to go back to. A preview version like
`0.2.0-preview.1` goes into Windows' version info as `0.2.0`, since that only takes numbers.

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
builds always finish. The packages are the run's artifacts (`orrery-windows-portable`, `orrery-windows-installer`,
`orrery-linux-portable`, `orrery-linux-packages`, `orrery-macos-portable`). A `v*` tag also makes a draft release
from them (see Releasing below); nothing else is published.

- **IronRDP.** The build jobs download the `Devolutions.IronRdp` that `build/ironrdp-package.json` names, with
  `build/Get-IronRdp.ps1`, which checks its SHA-256. Preflight fails straight away if the json's `version` isn't
  `IronRdpVersion` from `Directory.Build.props`, or it has no `url` or `sha256`: a new version has to be released from
  the fork first (below).
- **Lock files.** Each build job runs `dotnet restore Orrery.sln --locked-mode`, so the lock files must already record
  the published package, or the restore fails with NU1403.
- **Jobs.** Windows runs the tests, then builds the win-x64 and win-arm64 zips and Orrery Setup (Inno Setup 7.1.1).
  Linux builds the linux-x64 and linux-arm64 zips, .deb, .rpm and both AppImages (nfpm 2.48.0, appimagetool 1.9.1
  and the type2-runtime 20251108 runtimes, passed with `-AppImageRuntimeDir` so appimagetool doesn't download the
  newest one). macOS builds the macos-arm64 and macos-x64 zips.
- **Signing.** Release runs sign the Windows files through SignPath Foundation, and every run signs the .deb and
  .rpm with GPG, once each has its repository variables and secrets; until then those files are unsigned.
  [code-signing.md](code-signing.md) has the setup.
- **Pinned.** Every action is pinned by commit, every downloaded tool by version and SHA-256 (the workflow says where
  each hash came from), and the .NET SDK by `DOTNET_SDK_VERSION` (written to a `global.json` on the runner only, so
  local builds can use any .NET 10 SDK).
- **Caches.** NuGet packages are cached per OS as `nuget-<OS>-<hash of the lock files>`, without `Devolutions.IronRdp`.
- **Permissions.** The workflow's token can only read the repository (the Windows job can also read its run's
  artifacts, for SignPath), except in the release job, which runs only for
  `v*` tags and can write releases (`contents: write`) and make attestations (`id-token: write`,
  `attestations: write`).

## Releasing

Orrery checks GitHub's releases for updates, so a release has to look the way it expects:

1. Set `<Version>` in `Directory.Build.props` to the release's version (`0.2.0`) and commit it.
2. Tag that commit `v<version>` and push the tag: `git tag v0.2.0` then `git push origin v0.2.0`.
3. When the run finishes, its release job has made a **draft** release for the tag: every package, a `SHA256SUMS`
   listing their SHA-256s, notes generated from the commits since the last release, and GitHub artifact attestations
   for each package (`gh attestation verify <file> --repo InspireNXE/Orrery` checks one).
4. Look the draft over on GitHub (Releases), edit the notes, and publish it. Orrery never sees drafts.

**Previews.** A preview is a tag with a suffix on `<Version>`'s numbers, on whatever commit should go out:
`git tag v0.2.0-preview.1` then `git push origin v0.2.0-preview.1` while `<Version>` is `0.2.0` (or `0.2.0-dev`, or
`0.2.0-preview.1` itself). Every package is built as the tag's version, so a preview needs no commit of its own, and
`0.2.0-preview.1` < `0.2.0-preview.2` < `0.2.0`. The release job marks it a pre-release, and steps 3 and 4 are the
same. Orrery offers a pre-release to copies that turned previews on (Settings › About › Updates, or the setup), and to
a copy that's a preview itself unless previews were turned off there, so whoever installs a preview hears of the next
one. Once `0.2.0` is out, it's offered to them as well, being newer. The .deb and .rpm carry it as `0.2.0~preview.1`,
which their package managers also sort before `0.2.0`; macOS's Info.plist and Windows' version info get the numbers
only.

Preflight fails at once if the tag is neither `v` plus `<Version>` nor a preview of `<Version>`'s numbers: a tag for
another version needs `<Version>` bumped and committed first.

Every release needs its `SHA256SUMS`: Orrery checks each download against it (and against GitHub's own digest of
the file) and won't install a file that isn't listed or doesn't match. Don't rename the packages; Orrery picks its
file by name: `Orrery-Setup-<version>.exe` for an installed Windows copy, `Orrery-<version>-win-<x64|arm64>-portable.zip`
and `Orrery-<version>-linux-<x64|arm64>-portable.zip` for portable copies, `Orrery-<version>-<x86_64|aarch64>.AppImage`
for an AppImage. The .deb, .rpm and macOS copies only say there's a release and offer its page.

## Testing updates

`build/New-TestRelease.ps1` builds a release on your machine, so the whole update path can be tried before a real
release exists. It writes `artifacts/test-release/`: the packages, their `SHA256SUMS`, and `releases.json`, a release
list in the shape GitHub's API returns, naming the files relative to itself. Setting `ORRERY_UPDATE_FEED` to its path
makes Orrery read it instead of GitHub (the variable also takes an http(s) URL).

```powershell
./build/New-TestRelease.ps1 -Version 0.1.1      # this OS's portable zip, as version 0.1.1
./build/package.ps1 -Runtimes win-x64           # the older copy, at <Version> (0.1.0)
# Unzip artifacts/Orrery-0.1.0-win-x64-portable.zip into a folder of its own, then from the same shell:
$env:ORRERY_UPDATE_FEED = "$PWD/artifacts/test-release/releases.json"
& '<that folder>/Orrery.exe'                     # and check for updates
```

In Orrery, Ctrl+K `> Check for updates` (or Settings › About › Updates › Check now) checks at once; the automatic check
runs a few seconds after start-up when the last one was a day or more ago. A release found shows in a bar above the
session tabs, marked "(test feed)" while `ORRERY_UPDATE_FEED` is set. "See what's new" opens its notes, Update downloads
and checks it, and Orrery restarts as the new version, reopening the sessions that were open. With "When one is out:
Download it" in Settings, the automatic check downloads it in the background and the bar says it's ready.

The script prints these steps with the paths filled in. `-Runtimes` picks other platforms, `-Installer` also builds
Orrery Setup (Windows, with Inno Setup), `-AppImage` the AppImage (Linux, with appimagetool), `-Prerelease` marks the
release a pre-release, and `-BreakChecksum` writes wrong checksums, so the download should fail its check and be
deleted. For an installed copy, install the older setup (`build\Build-Installer.ps1`), set the variable for your user
(`[Environment]::SetEnvironmentVariable('ORRERY_UPDATE_FEED', '<path>', 'User')`), start Orrery from the Start menu,
and clear the variable afterwards.

## A new IronRDP package

`Devolutions.IronRdp` comes from the IronRDP fork ([Grinch/IronRDP](https://github.com/Grinch/IronRDP)) and isn't on
nuget.org. The fork releases each version Orrery uses: its workflow `.github/workflows/orrery-nuget.yml`, kept on the
fork's `master`, builds the package for the six platforms Orrery ships the way IronRDP's own `nuget-publish.yml` does
(leaving out Android, iOS and the macOS universal library), and publishes it as the release `nuget-<version>`, tagged
on the commit it was built from. `build/ironrdp-package.json` names that version, the fork and commit, the asset's
URL and its SHA-256. `build/Get-IronRdp.ps1` downloads it into `.nuget-local` and refuses a file whose SHA-256
differs. A published package is never replaced: everyone whose json names it would get a mismatch. A change to the
package always gets a new version.

1. Push the IronRDP commit to the fork. In `Directory.Build.props`, bump `IronRdpVersion`, for example from
   `2026.10.29-orrery.2` to `2026.10.29-orrery.3`.
2. On the fork, run the workflow by hand (Actions, Orrery NuGet package, Run workflow) with the commit's full SHA and
   that version. It fails at once if the commit isn't in the fork or the tag already exists. When it's done, it
   checks the uploaded asset's SHA-256 and prints the `version`, `repository`, `commit`, `url` and `sha256` for
   `build/ironrdp-package.json` (also in the run's summary).
3. Put those in `build/ironrdp-package.json`, then make the lock files record the package:

   ```powershell
   ./build/Get-IronRdp.ps1
   dotnet restore Orrery.sln --force-evaluate
   ```

   If `Get-IronRdp.ps1` warns that NuGet's cache holds a different package of the same version, delete the folder it
   names first, or the restore keeps using the old one. Commit the json, `Directory.Build.props` and the changed
   `packages.lock.json` files together, so the workflow never sees one without the others.

To try a package before releasing it, build it locally (or take the fork run's `ironrdp-nupkg` artifact) and use
`./build/Get-IronRdp.ps1 -FromFolder <folder> -Sha256 <its SHA-256>`, then `./build/Update-IronRdpLockHashes.ps1`.
Don't commit lock files made that way: a package built on one machine only carries that machine's native library
from the fork, and its hash won't match the release.

The first package (`2026.10.29-orrery.2`) was built by an Orrery workflow run before the fork had this workflow
(InspireNXE/Orrery run 41318600036, from `75e51a76`), and that same file was uploaded to the fork's release
`nuget-2026.10.29-orrery.2`.

## Running on Linux

None of the Linux builds has been run on a real machine yet. The .deb and .rpm pull in what they need. For the
portable zip and the AppImage, the machine needs:

- **A Wayland session with XWayland.** Orrery supports Wayland only. Avalonia 12.1's Wayland backend is experimental, so
  Orrery draws through X11 and runs under XWayland; Orrery itself has no X11-specific code and doesn't rely on X11
  behavior. X11 sessions aren't supported.
- **The ALSA library** (`libasound2t64` or `libasound2` on Debian and Ubuntu, `alsa-lib` on Fedora).
  IronRDP's native library links `libasound.so.2` for remote sound and won't load without it, so RDP fails to
  connect. The .deb and .rpm depend on it.
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
pass it, and the workflow sets it for every step) or `ContinuousIntegrationBuild=true` restore in locked mode and
fail with NU1004 if the packages no longer match the lock files. To check by hand: `dotnet restore Orrery.sln --locked-mode`.

## Icons and wizard art

`python build/generate-art.py` (needs Pillow) redraws `src/Orrery.App/Assets/orrery.{png,ico,icns}` and
`installer/media/WizardImage.png` from the logo in `MainWindow.axaml`.
