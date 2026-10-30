# Packet 15: Bound the CredSSP exchange, state RDP's NLA-only settings explicitly, and pin the prompts' fail-closed default

Rating: Mechanical. Findings: S-29, plus two hardening notes from the round (explicit NLA settings; `Reject` must stay
the default decision). Depends on: 09 (both edit `RdpSession.cs`; 09 itself follows 01).
Runner: `packet-sonnet-medium` (Sonnet, medium effort).

## Rules
- Work in your worktree branch, based on `main` after packet 09 has merged. Stage only files you changed, by explicit
  path; one commit.
- `ServerCertificateValidatorTests` and `HostKeyValidatorTests` are on the never-weaken list: add, don't loosen.
- Gate: `dotnet build Orrery.sln` with 0 errors and 0 warnings; focused tests while working; `dotnet test Orrery.sln`
  green at the end. New regression tests must be shown failing before the fix where that's possible.
- Final report: files changed, tests added, build and test summary, Verify evidence, other notes.

## Why
- **S-29 (plausible).** `RdpConnector.PerformCredsspAsync` reads each server reply with `framed.ReadByHint(hint, token)`
  (`src/Orrery.Protocols.Rdp/RdpConnector.cs:140-153`, read at `:146`). For a TsRequest using a 3- or 4-byte DER length,
  IronRDP's hint reports "size unknown" and `ReadByHint` keeps appending until it has a full PDU, with no size cap and only
  the session token to stop it. A server that passed the certificate check can keep the tab on "Connecting…" while the
  buffer grows with roughly quadratic CPU.
- **NLA settings.** `RdpSession.BuildConfig` (`src/Orrery.Protocols.Rdp/RdpSession.cs:505-520`) never sets `EnableTls`
  or `EnableCredssp`, and `RdpConnector` skips CredSSP without a word if the server selected plain TLS (`:49`). A probe
  showed today's IronRDP default asks for HYBRID and HYBRID_EX only and rejects an SSL selection, so this is safe now but
  rests on a default nobody wrote down.
- **Fail-closed default.** Closing either trust prompt returns `default(CertificateTrustDecision)`, which is `Reject`
  only because it's the first member (`src/Orrery.Core/Security/CertificateTrust.cs:7-12`). Nothing pins that.

## Changes
1. S-29: put a timeout (e.g. 30 s) around the whole CredSSP exchange using a linked `CancellationTokenSource`, and cap a
   single CredSSP reply at 64 KiB. If IronRDP's `Framed.ReadByHint` can't be capped, read the DER length yourself (the
   TsRequest is a DER SEQUENCE: `0x30`, then a length byte or `0x81`-`0x84` followed by that many length bytes) and use
   `ReadExact` with a checked size. Fail with "The server's sign-in reply was too large." or "The server took too long to
   sign in."
2. In `BuildConfig`, set `EnableCredssp = true` and `EnableTls = false` explicitly (check the property names in
   `Devolutions.IronRdp` 2026.10.29), with a comment that NLA is required. After the TLS upgrade in `RdpConnector`, if
   `ShouldPerformCredssp()` is false, fail instead of continuing without CredSSP.
3. Give `CertificateTrustDecision.Reject` an explicit `= 0` with a comment that closing a prompt relies on it, and add a
   test asserting `default(CertificateTrustDecision) == CertificateTrustDecision.Reject`.
4. Tests: the enum test above in `tests/Orrery.Core.Tests/Security/`. There's no RDP test project or fake server. Add
   unit tests for the DER-length helper if you write one (short form, 0x82, 0x84 with a huge value refused). Don't build an
   RDP server fake in this packet; report the timeout and the NLA check as covered by review.

## Verify
- New tests pass; the DER-length test fails before the helper exists.
- `dotnet test Orrery.sln` green.

## Do not touch
- Desktop size handling (packet 01) and the clipboard loop (packet 09).
- The certificate prompt's first-use default button: the verifier judged it a deliberate choice.
