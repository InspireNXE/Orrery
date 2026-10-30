# Packet 18: Move to the first IronRDP package that includes upstream fixes #2083, #2084 and #2096

Rating: Mechanical. Findings: S-7. Depends on: 01. **Blocked:** no such package is published yet.
Runner: `packet-sonnet-low` (Sonnet, low effort).

## Rules
- Run only once `Devolutions.IronRdp` has a NuGet release built from a commit that contains all three fixes. Check the
  package's nuspec for its source commit, and the GitHub history of `Devolutions/IronRDP` for the merge commits:
  #2084 `9409486b`, #2083 `134493e`, #2096 `56d691e3` (all merged 2026-10-30).
- Work in your worktree branch, based on `main` after packet 01 has merged. Stage only files you changed, by explicit
  path; one commit.
- Gate: `dotnet build Orrery.sln` with 0 errors and 0 warnings; `dotnet test Orrery.sln` green; a live connection to an
  RDP server (Windows host or VM) that the owner runs and reports back. A live check that can't run is reported as not
  run.
- Final report: files changed, build and test summary, Verify evidence, any API changes needed.

## Why
`Directory.Build.props:17` pins `IronRdpVersion` 2026.10.29, built from IronRDP commit c3a782ff (2026-10-29). Upstream
merged three fixes for hostile-server input the next day:
- #2084 rejects out-of-range server desktop sizes. Before it, the server's size went into an infallible native
  allocation that aborts the process on failure.
- #2083 bounds compressed static-channel reassembly. Before it, the buffer grew without limit. Orrery attaches cliprdr
  (`src/Orrery.Protocols.Rdp/RdpConnector.cs:30-33`) and always attaches the dynamic-channel transport (`:29`), so turning
  clipboard sharing off doesn't avoid it.
- #2096 stops RFX geometry wrapping u16.
Even after them, upstream still accepts 32766x32766 and about 4 GiB of reassembly, so packet 01's own size cap stays.

## Changes
1. Bump `IronRdpVersion`. Fix any API changes the new package needs, keeping behaviour the same.
2. If packet 19 has merged, run `dotnet restore Orrery.sln --force-evaluate` and commit the updated
   `packages.lock.json` files with the bump.
3. Confirm in the new native DLL that #2084's error text ("negotiated desktop size ... is outside the supported range")
   is present.

## Verify
- Build and tests green; the live RDP connection works (connect, type, resize, clipboard, disconnect).

## Do not touch
- Packet 01's size cap.
