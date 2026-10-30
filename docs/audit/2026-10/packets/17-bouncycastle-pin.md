# Packet 17: Reference BouncyCastle 2.7.0 directly instead of getting it by chance through SSH.NET

Rating: Mechanical. Findings: S-34. Depends on: nothing.
Runner: `packet-sonnet-low` (Sonnet, low effort).

## Rules
- Work in your worktree branch, based on `main`. Stage only files you changed, by explicit path; one commit.
- Gate: `dotnet build Orrery.sln` with 0 errors and 0 warnings; `dotnet test tests/Orrery.Core.Tests --filter "FullyQualifiedName~Import"`;
  `dotnet test Orrery.sln` green at the end.
- Final report: files changed, build and test summary, Verify evidence, other notes.

## Why
`src/Orrery.Core/Orrery.Core.csproj:3` references `BouncyCastle.Cryptography` 2.6.2, which is affected by the 21
BouncyCastle advisories published 2026-10-02 (CVE-2026-63569, CVE-2026-103600 and others), all fixed in 2.7.0. The app
ships 2.7.0 only because SSH.NET 2026.0.0 requires at least 2.7.0 (`src/Orrery.App/obj/project.assets.json` resolves
2.7.0; `src/Orrery.Core/obj/project.assets.json` resolves 2.6.2). NuGet's advisory feed doesn't map these yet, which is
why `dotnet list package --vulnerable` came back clean. Orrery's own use (AES-GCM and PBKDF2 in the mRemoteNG importer)
doesn't touch the affected code.

## Changes
1. Change the reference to 2.7.0. If `Directory.Build.props` defines versions for other packages (it defines
   `AvaloniaVersion` and `IronRdpVersion`), follow that pattern with a `BouncyCastleVersion` property.
2. Restore and confirm both `Orrery.Core` and `Orrery.App` now resolve 2.7.0 (check each `obj/project.assets.json`).

## Verify
- `dotnet list Orrery.sln package --include-transitive` shows BouncyCastle.Cryptography 2.7.0 for every project that uses it.
- `dotnet test Orrery.sln` green (the import crypto tests in particular).

## Do not touch
- Other package versions. Lock files and NuGet source mapping are a separate owner decision (see the README).
