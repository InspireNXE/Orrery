# Packet 02: Stop the VNC client sending the saved password in clear text

Rating: Decision. Findings: S-2. Depends on: 01 (both edit `VncSession.cs`).
Runner: `packet-opus-high` (Opus, high effort). A wrong type list sends real credentials in the clear.

**Answer (2026-10-30):** A, only None and classic VNC authentication. Servers that require VeNCrypt, TLS or RA2 stop
connecting until a later change wires those types to the certificate prompt.

## Question
The VNC library (`Community.MarcusW.VncClient` 2.0.8, a one-person fork) registers 13 security types and lets the
server pick. Several of them send the saved username and full password in clear text, and the TLS-based ones accept any
certificate. Which do we allow?

- **A. Only None and classic VNC authentication (recommended).** This is what the original library registers. Servers
  that require VeNCrypt, TLS or RealVNC's RA2 stop working until a later change wires those to Orrery's certificate
  prompt. Small change, closes S-2 completely.
- **B. Keep TLS-based types, but validate them.** Route the TLS and VeNCrypt certificate through
  `ServerCertificateValidator` and the existing prompt, show RA2's RSA key as a host-key style prompt, and still block
  the clear-text sub-types (IntegratedSSH, VeNCrypt Plain, SASL PLAIN, Tight login). Much more work, depends on library
  internals that may not expose the hooks, and a man in the middle can still strip the encrypted types and fall back to
  classic auth.

## Rules
- Work in your worktree branch, based on `main` after packet 01 has merged. Stage only files you changed, by explicit path; one commit.
- Gate: `dotnet build Orrery.sln` with 0 errors and 0 warnings; `dotnet test tests/Orrery.Protocols.Vnc.Tests` while
  working; `dotnet test Orrery.sln` green at the end. New regression tests must be shown failing before the fix.
- Final report: files changed, tests added, build and test summary, Verify evidence, other notes.

## Why
`VncSession.ConnectAsync` builds `new VncClient(_logging, new ClosableImplementation(this))`
(`src/Orrery.Protocols.Vnc/VncSession.cs:88`), and `ClosableImplementation` (`:474-477`) derives from
`DefaultImplementation` without changing its security types. In 2.0.8 those are, by priority: VeNCrypt 100,
IntegratedSSH 95, SecureTunnel 90, TLS 80, RA2 70, SASL 60, RA2ne 50, Tight 40, Ultra 30, XVP 20, MD5 15, VncAuth 10,
None 1. The handshaker takes the highest one the server offers (under RFB 3.3 the server simply names one), and
`Authenticator` (`:491-500`) hands username and password to any type that asks. The verifier drove Orrery's own
`VncSession.Start` against a stand-in server offering `[2, 24]`: the client picked IntegratedSSH, a stub that sends a
fake key exchange and then the credentials as plain UTF-8, and the tab showed Connected. VeNCrypt Plain, SASL (PLAIN for
anything but CRAM-MD5), Tight login and the TLS types with a self-signed certificate leaked the password too. The
credential is often a vault credential reused elsewhere (`src/Orrery.App/MainWindow.axaml.cs:891-904, 938, 993-995`).
Classic VNC auth (DES, 8 characters) is an accepted risk; this is far worse.

## Changes (option A, approved)
1. Pass a security-types builder through the `DefaultImplementation` constructor overload that takes one (check its
   signature in the 2.0.8 package; the verifier found a three-argument constructor). Return only
   `NoneSecurityType` and `VncAuthenticationSecurityType`.
2. Make `Authenticator.ProvideAuthenticationInputAsync` refuse anything but the classic VNC auth type: throw an
   exception that fails the connection with a message such as "The server asked for a login method Orrery doesn't
   support." Never return `CredentialsAuthenticationInput`.
3. Leave a short comment at the builder saying why the list is restricted, naming S-2, so nobody re-enables the defaults.
4. Tests in `tests/Orrery.Protocols.Vnc.Tests/VncSessionTests.cs` with `TestRfbServer` (extend it to offer a chosen list
   and record what the client sends):
   - Server offers `[2, 24]`: client picks 2 and sends only a 16-byte DES response.
   - Server offers only `[24]`, `[19]`, `[20]` or `[16]` (and RFB 3.3 naming 24): the session fails, and the password
     bytes never appear in what the server received.
   - Server offers `[1]`: still connects (accepted risk).

## Verify
- The tests above pass and fail on `main`.
- `dotnet test Orrery.sln` green.

## Do not touch
- The framebuffer code (packet 01) and clipboard code (packet 09).
