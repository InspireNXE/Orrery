# Packaging Orrery

Two kinds of package, both self-contained (the .NET runtime ships beside the app, so nothing else needs installing).

## Orrery Setup (Windows installer)

Built with Inno Setup, the same way as Prober's desktop setup (`installer\Orrery.iss` is modelled on
`Prober.Installer\Prober.Desktop.iss`, and `installer\WizardLayout.iss` is copied from Prober so the wizard looks
the same). You need PowerShell 5.1 or later, the .NET SDK, and Inno Setup 7 installed per user
(`%LOCALAPPDATA%\Programs\Inno Setup 7\ISCC.exe`) or Inno Setup 6 under either Program Files folder; pass
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
([design doc](design-storage-vault-import.md) §2.2). On Windows and Linux the app is one executable (`Orrery.exe`,
about 60 MB). Its native libraries (Skia, HarfBuzz, ANGLE, IronRDP, libsodium) can't load from inside it, so the
first launch unpacks them to `%TEMP%\.net\Orrery\<hash>` (`~/.net/Orrery` on Linux); set
`DOTNET_BUNDLE_EXTRACT_BASE_DIR` to put them elsewhere. The single-file analyzer is turned off for that publish,
because it would add a package that isn't in the lock files. On macOS the zip holds `Orrery.app` (Info.plist and icon),
with the marker beside the bundle. The bundle isn't signed or notarised yet, so macOS asks before the first launch.
Zip files carry no Unix permissions from Windows: on Linux and macOS, `chmod +x` the executable after unzipping, or
build those packages on Linux or macOS.

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
nuget.org only, with every package mapped to it.

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
