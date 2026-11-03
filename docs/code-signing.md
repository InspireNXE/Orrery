# Code signing

A plan for signing Orrery's packages, drafted 2026-11-03. Nothing here is set up yet: today no package is signed,
Edge's Defender heuristics flag the Windows download as `Trojan:Script/Wacatac.B!ml` ([packaging](packaging.md)),
macOS asks before the first launch, and Touch ID unlock waits for a signed build. Prices and terms below were read
from each provider's own pages on that date; links are at the end.

## What to do, in order

| # | Step | Platforms | Cost | Owner's part |
|---|---|---|---|---|
| 1 | GitHub build attestations | All | Free | None: a workflow change |
| 2 | SignPath Foundation | Windows | Free | Apply; name the approvers; approve each release |
| 3 | GPG-signed packages | Linux | Free | Make and keep a signing key |
| 4 | Developer ID and notarization | macOS | $99 a year | Decide whether macOS is worth it; enroll |

Steps 1 to 3 cost nothing and can start now. Step 4 is the only one with no free route.

## 1. Build attestations (all platforms)

GitHub signs a record of which workflow, commit and runner built each file, kept in the public Sigstore log. Free for
public repositories. Anyone can check a download with `gh attestation verify <file> --repo InspireNXE/Orrery`. It
doesn't quiet SmartScreen or Gatekeeper, but it proves a release came from this repository's CI, which suits a
security-minded project, and it covers the Linux and macOS files that nothing else signs yet.

In `.github/workflows/packages.yml`, give each packaging job `id-token: write` and `attestations: write` (with
`contents: read`) and add, after the packages are built:

```yaml
      - name: Attest the packages
        uses: actions/attest@v4   # pin to the commit SHA of v4.2.2, like the workflow's other actions
        with:
          subject-path: artifacts/**/*.zip   # and the installer, .deb, .rpm, AppImage per job
```

## 2. SignPath Foundation (Windows)

SignPath Foundation signs open-source projects' Windows files for free with its own certificate, kept in its
hardware; the files show **"SignPath Foundation"** as the publisher, not InspireNXE. It's the only free certificate
that works from CI, and signing is the likeliest fix for the Defender flag. SmartScreen still warns "unrecognized"
until the signed files build a download history: Microsoft says that takes weeks and hundreds of clean installs, and
since 2024 even EV certificates don't skip it. Keeping one signing identity from release to release keeps what's built.

**Does Orrery qualify?** As far as its terms go, yes:

- MIT, an OSI license, with no commercial dual license.
- Every bundled component is open source: Avalonia, the web view package, SSH.NET, IronRDP, NSec and libsodium,
  BouncyCastle, Serilog and the .NET runtime are MIT, Apache, BSD or ISC (`THIRD-PARTY-NOTICES.txt`). The web view
  uses the WebView2 runtime that comes with Windows, which counts as a system library; no WebView2 SDK is bundled.
- No telemetry. Host checks contact the user's own hosts and can be turned off, in Settings or per folder.
- The installer uninstalls cleanly.
- Builds run on GitHub-hosted runners, which SignPath requires for every job leading to a signing request.

**What it asks of the project:**

1. Multi-factor sign-in for everyone on the GitHub repository and on SignPath.
2. Named roles: authors, reviewers, and approvers. Every signing request is approved by hand.
3. A **"Code signing policy"** section on the project's home page (the README will do). A draft is below.
4. Product name and version set in every signed file. Already done: `Directory.Build.props` sets `<Version>` and
   `<Company>`, `Orrery.App.csproj` sets `<Product>`, and `installer/Orrery.iss` sets `VersionInfoVersion` and the
   publisher.
5. Only versions that are actually released get signed, and the download page says what Orrery does.

**Applying:** at signpath.org/apply. There's no published approval time; other projects report waits of weeks.

**Wiring it in:** the Windows job uploads the unsigned files with `actions/upload-artifact`, then
`signpath/github-action-submit-signing-request@v3` sends that artifact's ID with the organization and project slugs and
an API token (a repository secret), waits for approval, and downloads the signed files. The artifact configuration
lists what to sign inside the zip: `Orrery.exe` and then the setup that contains it. That means building the installer
after `Orrery.exe` is signed, so the job becomes publish, then sign the exe, then compile the setup, then sign the
setup. Inno Setup also writes an uninstaller into the setup, which ideally is signed too; SignPath's documents don't
say how they handle that, so it's a question for the application.

**Draft policy section for the README:**

> ## Code signing policy
>
> Free code signing provided by [SignPath.io](https://about.signpath.io/), certificate by
> [SignPath Foundation](https://signpath.org/).
>
> - Committers and reviewers: [members of this repository](https://github.com/InspireNXE/Orrery/graphs/contributors)
> - Approvers: Steven Downer
>
> Every signed file is built by this repository's GitHub Actions from a tagged release, and each signing request is
> approved by hand.
>
> Privacy: Orrery sends nothing to its authors or anyone else. It connects only to the hosts you set up, and host
> checks (a quick connection to show whether a host is up) can be turned off in Settings or for a folder.

### The paid alternatives, for comparison

- **Microsoft Artifact Signing** (Trusted Signing until 2026): about $9.99 a month, from GitHub Actions
  (`azure/artifact-signing-action`), with the publisher in your own name. Individuals have to be in the US or Canada.
  Organizations qualify in more countries but need a validated legal entity.
- **Certum Open Source**: from €49 a year on SimplySign, in a personal name prefixed "Open Source Developer". Each
  signing session needs a code from a phone app, so it doesn't run unattended in CI.
- **Microsoft Store**: listing is now free for individuals, but for a regular EXE or MSI installer the Store doesn't
  sign it. The installer has to be signed already, so the Store doesn't avoid needing a certificate.

## 3. Linux packages

Nothing in Linux requires a signature, and signing is free:

- **.deb and .rpm**: sign them with a GPG key kept as a repository secret, and publish its public key in the
  releases. If Orrery later offers an apt or dnf repository, its index is what apt checks: users put the key in
  `/etc/apt/keyrings` and name it with `signed-by`. Since February 2026, Debian 13's apt rejects SHA-1 signatures, so
  use a fresh Ed25519 or RSA-4096 key and SHA-256.
- **AppImage**: `appimagetool --sign` embeds a GPG signature, but nothing checks it when the AppImage runs, so the
  attestation in step 1, and a published checksum, carry more weight.

## 4. macOS

There is no free route. A Developer ID certificate and notarization (Apple scanning and approving each build) need
the Apple Developer Program, **$99 a year**. Apple's fee waiver covers nonprofits, schools and government bodies,
not individuals. What it would buy:

- No Gatekeeper prompt: today, macOS 15 sends users to System Settings > Privacy & Security to allow Orrery.
- Touch ID unlock, which needs a signed build.
- Homebrew: since September 2026 it disables casks that fail Gatekeeper, so an unsigned Orrery can't be a cask.

If it's done, the macOS job would sign `Orrery.app` with the hardened runtime and an entitlements file, put it in a
DMG (a ZIP can't carry the notarization ticket), notarize it with `xcrun notarytool` using an App Store Connect API
key kept as a secret, and staple the ticket with `xcrun stapler`. The certificate is exported as a .p12 secret and
imported into a temporary keychain on the runner. The entitlements need checking against what Orrery does: the
network, the keychain, and the IronRDP and libsodium native libraries, which the hardened runtime may need allowed.

## Sources

- SignPath Foundation terms: https://signpath.org/terms
- SignPath with GitHub Actions: https://docs.signpath.io/trusted-build-systems/github
- Microsoft Artifact Signing: https://learn.microsoft.com/azure/artifact-signing/quickstart
- SmartScreen reputation: https://learn.microsoft.com/windows/apps/package-and-deploy/smartscreen-reputation
- Store requirements for EXE and MSI apps: https://learn.microsoft.com/windows/apps/publish/publish-your-app/msi/app-package-requirements
- Certum Open Source on SimplySign: https://shop.certum.eu/open-source-code-signing-on-simplysign.html
- Apple fee waiver: https://developer.apple.com/support/membership-fee-waiver/
- Apple Developer ID: https://developer.apple.com/developer-id/
- Homebrew 5.0.0 (Gatekeeper deprecation): https://brew.sh/2025/11/12/homebrew-5.0.0/
- GitHub artifact attestations: https://docs.github.com/actions/concepts/security/artifact-attestations
- Debian third-party repositories: https://wiki.debian.org/DebianRepository/UseThirdParty
