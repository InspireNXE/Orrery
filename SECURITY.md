# Security

Orrery stores passwords and keys and talks to remote machines, so security reports are welcome.

## Reporting a vulnerability

Please report it privately through GitHub: on this repository's **Security** tab, choose **Report a vulnerability**.
Don't open a public issue for it.

Include what you found, how to reproduce it, and which version or commit you tested. You'll get a reply within a
week. Once a fix is out, the report is published as a GitHub security advisory, with credit to you unless you'd
rather not.

## What's covered

- Orrery itself: everything in this repository.
- How Orrery uses its libraries, such as IronRDP, SSH.NET, MarcusW.VncClient and NSec. A bug inside one of those
  libraries is best reported to its own project as well.

Orrery is at version 0.1.0, and only the latest commit on `main` gets fixes.

## Past audits

[docs/audit/2026-10](docs/audit/2026-10) holds a security audit from October 2026 and the fixes for what it found.
[docs/audit/2026-11](docs/audit/2026-11) covers what came after it: web tabs, session recording, the RDP clipboard and
shared folders, imports and the CI build. Both were run with AI assistance (see the README's
[How it's made](README.md#how-its-made)), and what they left open is listed in each round's `round.md`.
