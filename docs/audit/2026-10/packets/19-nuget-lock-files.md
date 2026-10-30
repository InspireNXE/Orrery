# Packet 19: Lock NuGet restores to recorded versions and map packages to nuget.org

Rating: Mechanical. Findings: none (owner request, 2026-10-30, after S-34). Depends on: 17 (it changes a package
version, and the lock files should record the fixed one).
Runner: `packet-sonnet-medium` (Sonnet, medium effort).

## Rules
- Work in your worktree branch, based on `main` after packet 17 has merged. Stage only files you changed, by explicit
  path; one commit.
- Gate: `dotnet restore Orrery.sln --locked-mode` succeeds; `dotnet build Orrery.sln` with 0 errors and 0 warnings in
  Debug and Release; `dotnet test Orrery.sln` green.
- Final report: files changed, build and test summary, Verify evidence, other notes.

## Why
Nothing records the exact packages a build used. There is no `packages.lock.json`, no `nuget.config` in the repo, and
restores use whatever sources the machine has. On the owner's machine that's nuget.org plus
`C:\Program Files (x86)\Microsoft SDKs\NuGetPackages\`. A transitive dependency can change between two builds of the
same commit, as S-34 showed: BouncyCastle 2.6.2 or 2.7.0 depending on which project resolved it. A package of the same
name on another configured source can also be picked up (dependency confusion). Orrery depends on single-maintainer
packages (`Community.MarcusW.VncClient` 2.0.8) and a native IronRDP build, so a silent change matters.

## Changes
1. In `Directory.Build.props`, add `<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>`, and
   `<RestoreLockedMode Condition="'$(ContinuousIntegrationBuild)' == 'true' or '$(OrreryLockedRestore)' == 'true'">true</RestoreLockedMode>`
   so developers can still add packages locally while release builds refuse a drifted graph.
2. Add `nuget.config` at the repo root: `<clear />` the sources, add only nuget.org
   (`https://api.nuget.org/v3/index.json`), and add `<packageSourceMapping>` with `<package pattern="*" />` under
   nuget.org.
3. Remove the Debug-only `Avalonia.Diagnostics` reference (`src/Orrery.App/Orrery.App.csproj:23`). A reference with a
   `Configuration` condition gives Debug and Release different graphs, which breaks locked mode, and nothing calls
   `AttachDevTools` (grep `src` to confirm before removing it). If it turns out to be used, keep it unconditional instead
   and say so in the report.
4. Self-contained publishes add runtime packs for each RID, which go in the lock file too. In
   `src/Orrery.App/Orrery.App.csproj`, set `<RuntimeIdentifiers>` to the list in `build/package.ps1:20`
   (`win-x64;win-arm64;linux-x64;linux-arm64;osx-arm64;osx-x64`), so one lock file covers every publish.
5. Run `dotnet restore Orrery.sln --force-evaluate` and commit every generated `packages.lock.json` (one per project under
   `src/` and `tests/`). Check that none of them lists BouncyCastle 2.6.2.
6. Pass `-p:OrreryLockedRestore=true` in the `dotnet publish` calls in `build/package.ps1:40` and
   `build/Build-Installer.ps1:70-73`.
7. Add a short section to `docs/` where build steps are described (or to the README if there is no such doc) saying
   that a package change needs `dotnet restore --force-evaluate` and the updated lock files in the same commit.

## Verify
- `dotnet restore Orrery.sln --locked-mode` succeeds on a clean clone (`git clean -xdf` in the worktree first).
- Locked mode fails when you change a version: bump `xunit` in one test project, run locked restore, show the
  NU1004 error, then undo it.
- `build/package.ps1 -Runtimes win-x64` and a `linux-x64` publish both succeed with locked restore.
- `dotnet nuget list source --configfile nuget.config` lists only nuget.org.
- `dotnet test Orrery.sln` green.

## Do not touch
- Package versions, except removing `Avalonia.Diagnostics`. Version bumps are packets 17 and 18.
