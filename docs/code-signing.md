# Code signing

A plan for signing Orrery's packages, drafted 2026-11-03. Nothing here is set up yet: today no package is signed,
Edge's Defender heuristics flag the Windows download as `Trojan:Script/Wacatac.B!ml` ([packaging](packaging.md)),
macOS asks before the first launch, and Touch ID unlock waits for a signed build. Prices and terms below were read
from each provider's own pages on that date; links are at the end.

## What to do, in order

| # | Step | Platforms | Cost | Owner's part | Status |
|---|---|---|---|---|---|
| 1 | GitHub build attestations | All | Free | None: a workflow change | Done (0.1.0 on) |
| 2 | SignPath Foundation | Windows | Free | Apply; name the approvers; approve each release | Workflow ready; applying needs some reputation first |
| 3 | GPG-signed packages | Linux | Free | Make and keep a signing key | Workflow ready; waiting on the key |
| 4 | Developer ID and notarization | macOS | $99 a year | Decide whether macOS is worth it; enroll | Not started |
| 5 | Microsoft-signed drivers | Windows, Orrery Host only | EV certificate, about $280 to $350 a year, plus cloud signing | A registered organization; buy the certificate; register in Partner Center | Not started; phase 6 of the stream design |

Steps 1 to 3 cost nothing. Step 4 is the only one with no free route for the app. Step 5 matters only for the
streaming host's drivers, not for Orrery itself. Until a step's secrets exist, the workflow builds that platform's
files unsigned, as before, and a release run says so in a warning.

## 1. Build attestations (all platforms)

GitHub signs a record of which workflow, commit and runner built each file, kept in the public Sigstore log. Free for
public repositories. Anyone can check a download with `gh attestation verify <file> --repo InspireNXE/Orrery`. It
doesn't quiet SmartScreen or Gatekeeper, but it proves a release came from this repository's CI, which suits a
security-minded project, and it covers the Linux and macOS files that nothing else signs yet.

The release job in `.github/workflows/packages.yml` does this: it attests every file listed in the release's
`SHA256SUMS` with `actions/attest`. To check a download:

```bash
gh attestation verify Orrery-Setup-0.1.0.exe --repo InspireNXE/Orrery
```

It prints the workflow, commit and tag the file was built from, and fails for a file that wasn't built there or was
changed since. Without the GitHub CLI, compare the file's SHA-256 with its line in the release's `SHA256SUMS`.

## 2. SignPath Foundation (Windows)

SignPath Foundation signs open-source projects' Windows files for free with its own certificate, kept in its
hardware; the files show **"SignPath Foundation"** as the publisher, not InspireNXE. It's the only free certificate
that works from CI, and signing is the likeliest fix for the Defender flag. SmartScreen still warns "unrecognized"
until the signed files build a download history: Microsoft says that takes weeks and hundreds of clean installs, and
since 2024 even EV certificates don't skip it. Keeping one signing identity from release to release keeps what's built.

**Does Orrery qualify?** As far as its terms go, yes:

- MIT, an OSI license, with no commercial dual license.
- Every bundled component is open source: Avalonia, the web view package, SSH.NET, IronRDP, NSec and libsodium,
  BouncyCastle, Serilog, Markdig and the .NET runtime are MIT, Apache, BSD or ISC (`THIRD-PARTY-NOTICES.txt`). The web view
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

**Applying:** at signpath.org/apply. There's no published approval time; other projects report waits of weeks. The
application asks for evidence the project is established and used (its history, releases, downloads, users), so a
project with one release may have to wait until it has more to show. Meanwhile Windows releases go out unsigned,
with their attestations and `SHA256SUMS`; the paid options below don't need any reputation, if waiting costs too much.

**How the workflow does it** (the Windows job, on `v*` tags only, since each request is approved by hand):

1. Builds the portable zips, and publishes the installer's files without building the setup yet
   (`build/Build-Installer.ps1 -PublishOnly`).
2. Uploads Orrery's own files as one artifact: each portable `Orrery.exe`, and the installed copy's `Orrery.exe`,
   `Orrery.dll` and `Orrery.*.dll`. Third-party DLLs keep their own signatures, or none.
3. `signpath/github-action-submit-signing-request` (v3.0, pinned) submits it under the artifact configuration
   `files` and waits up to an hour for approval, then downloads the signed files.
4. Puts them back, checks each one's Authenticode signature, zips the portable folders again
   (`build/package.ps1 -RezipOnly`) and builds Orrery Setup from the signed files (`-SkipPublish`).
5. Submits the setup under the artifact configuration `setup`, checks it, and uploads everything as before.

So a release asks for two approvals: the files, then the setup. Inno Setup also writes an uninstaller into the setup,
which isn't signed this way; SignPath's documents don't say how they handle that, so it's a question for them.

**Setting it up, once accepted:**

1. In the SignPath project, add two artifact configurations, pasting `build/signpath/files.xml` with the slug
   `files` and `build/signpath/setup.xml` with the slug `setup`. SignPath's editor checks them; restrictions on
   product name and version can be added there.
2. Use the signing policy SignPath Foundation sets up for releases, with the slug `release-signing` (or change
   `signing-policy-slug` in the workflow to match), and link the project to this repository as its trusted build
   system (GitHub.com).
3. Make an API token for a CI user in SignPath, with permission to submit to that policy.
4. In the GitHub repository, Settings > Secrets and variables > Actions:
   - Variables: `SIGNPATH_ORGANIZATION_ID` (the organization's ID) and `SIGNPATH_PROJECT_SLUG`.
   - Secret: `SIGNPATH_API_TOKEN`.

   Or from a terminal: `gh variable set SIGNPATH_ORGANIZATION_ID`, `gh variable set SIGNPATH_PROJECT_SLUG` and
   `gh secret set SIGNPATH_API_TOKEN`, each asking for its value.
5. Add the "Code signing policy" section below to the README.
6. The next tag's run waits at "Sign the files" for approval in SignPath, then at "Sign the setup".

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

- **.deb and .rpm**: signed by nfpm with a GPG key kept as a repository secret (`build/Build-LinuxPackages.ps1
  -SigningKey`). The .rpm gets a header signature, which `rpm -K` checks; the .deb a detached `_gpgorigin` signature
  (debsigs' "origin" type) over its other members. The workflow checks both against `build/linux-signing-key.asc`,
  the public half, before uploading, and the release carries it as `orrery-linux-signing-key.asc`. If Orrery later
  offers an apt or dnf repository, its index is what apt checks: users put the key in `/etc/apt/keyrings` and name it
  with `signed-by`. Since February 2026, Debian 13's apt rejects SHA-1 signatures, so the key is a fresh RSA-4096
  one (RSA rather than Ed25519, which rpm before 4.16, as on RHEL 8, can't check).
- **AppImage**: `appimagetool --sign` embeds a GPG signature, but nothing checks it when the AppImage runs, so the
  attestation in step 1, and a published checksum, carry more weight.

**Making the key** (once, on your own machine; the secret half never goes in the repository). On Windows, Git for
Windows' gpg does it, but it isn't on PowerShell's PATH; its passphrase prompts are small Windows dialogs. In
PowerShell, from the repository folder:

```powershell
$gpg = 'C:\Program Files\Git\usr\bin\gpg.exe'
& $gpg --quick-generate-key "InspireNXE Orrery packages" rsa4096 sign 3y
```

It asks for a passphrase twice, then prints the key with its 40-character fingerprint. With that as `$fpr`:

```powershell
$fpr = '<the fingerprint>'
& $gpg --armor --export $fpr | Set-Content -Encoding ascii build\linux-signing-key.asc
& $gpg --armor --export-secret-keys $fpr | gh secret set LINUX_SIGNING_KEY --repo InspireNXE/Orrery
gh secret set LINUX_SIGNING_PASSPHRASE --repo InspireNXE/Orrery
```

Exporting the secret key asks for the passphrase again; the last command asks for it to store. On Linux or macOS
it's the same with `gpg` and `>` in place of `Set-Content`. Commit `build/linux-signing-key.asc`, and push to `dev` first: the Linux job then
signs and checks the packages, so a problem shows before a release. Keep a backup of the secret key and of the
revocation certificate gpg wrote to `~/.gnupg/openpgp-revocs.d/`. Before the key expires, extend it
(`gpg --quick-set-expire FPR 3y`), export the public half again, and update the secret.

To check a downloaded package:

```bash
gpg --import orrery-linux-signing-key.asc && sudo rpm --import orrery-linux-signing-key.asc
rpm -K orrery-0.1.1-1.x86_64.rpm      # "digests signatures OK"
```

For a .deb, `debsig-verify` needs a policy file for the key; comparing against `SHA256SUMS` or the attestation is
simpler.

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

## 5. Drivers (Orrery Host, Windows)

Added 2026-11-09, from Microsoft's driver-signing pages as updated between April and August 2026. Orrery itself
installs no drivers, and neither will the Linux host (uinput and PipeWire need none). Phase 6 of the stream design
(`docs/design-stream.md` on the `design/stream-protocol` branch) adds three to the Windows host:

| Driver | Runs in | Microsoft's signature | Without it |
|---|---|---|---|
| XInput-compatible gamepad | Kernel (a bus driver, as ViGEmBus was) | Required | Gamepads through `InputInjector.InjectGamepadInput` or a user-mode HID device: games that use Windows.Gaming.Input see them, XInput-only games don't |
| IddCx virtual display, for headless hosts | User mode (UMDF 2) | Not settled; see below | The host streams a real monitor, or a dummy plug |
| Virtual audio: microphone and speaker | Kernel (an audio miniport) | Required | Host sound already works through WASAPI loopback; only the virtual microphone waits |

**Why the kernel drivers can't avoid it.** Since Windows 10 1607, with Secure Boot on, Windows loads a new kernel
driver only if Microsoft signed it. Cross-signed drivers still load only with Secure Boot off, on machines upgraded
from before 1607, or with certificates issued before 29 July 2015. Test signing needs Secure Boot off too. All of
that is fine for development and not something to ask users to do.

**The user-mode display driver.** Microsoft's pages don't say plainly whether a UMDF driver package installs on
Windows 10 and 11 with only a third-party Authenticode signature. Older Microsoft guidance says user-mode drivers
skip kernel code integrity but still need a signed package to install. Before counting on that, sign a build of the
IddCx driver the way the app is signed (step 2) and install it on a machine with Secure Boot on. If it installs
with only a publisher prompt, the display driver can ship before the rest. Attestation signing covers user-mode
drivers too, so it can go through Partner Center with the other two once that exists.

**How other streaming projects do it** (their READMEs and docs, read 2026-11-09):

- **Virtual Display Driver** (VirtualDrivers), an IddCx driver from Microsoft's sample like ours, is signed by
  **SignPath Foundation**. Its README names no Microsoft attestation, WHQL or company, and mentions test signing only
  for ARM64 on Windows 11 24H2. That's good evidence the display driver can follow step 2; the test above still
  stands, since the README doesn't rule out attestation as well.
- **Apollo** (a Sunshine fork) bundles **SudoVDA**, a fork of Virtual Display Driver. Neither says how it's signed.
- **Sunshine** writes no kernel driver of its own that it gives away:
  - Gamepads: ViGEmBus, now at end of life, or LizardByte's **Virtual HID driver**, a paid add-on licensed per
    machine. The paid driver is how they cover Microsoft signing.
  - Audio: it installs **Valve's Steam Streaming Speakers** driver when Steam is there, or uses a third-party
    virtual audio cable. No audio driver of its own.
  - Display: none shipped. Its docs suggest a dummy HDMI plug, and its settings work with a separate virtual display
    driver.
- **ViGEmBus** was retired and archived in November 2023. Its signed builds came from **Nefarius Software Solutions
  e.U.**, an Austrian registered sole trader. So the "registered organization" in step 1 below may be one person's
  registered business, not a company. (An inference: the README doesn't say which Microsoft route signed it.)
- **Moonlight** is a client and needs no drivers, like Orrery and Orrery Play.

What that means here: the display driver can go the way Virtual Display Driver does. Valve's driver can stand in
for host sound on machines with Steam, but not for the virtual microphone. And with ViGEmBus gone, there's no free
signed XInput driver left to rely on: the gamepad driver needs our own Microsoft signature, paid for one way or
another.

**What Microsoft's signature takes:**

1. **A registered organization.** Registering for the Windows Hardware Developer Program asks for the company's
   details or a D-U-N-S number and a legal contact who can sign for the company. Individuals aren't mentioned, and EV
   certificates are only issued to organizations, so InspireNXE has to be a legal entity a certificate authority
   can verify. This is the real gate, and it comes before any money is spent.
2. **An EV code-signing certificate.** Registration requires one, and attestation submissions have to be signed
   with one. Artifact Signing (step 2's paid alternative) isn't accepted. Microsoft lists Certum, DigiCert,
   GlobalSign, IdenTrust, Sectigo and SSL.com. Prices seen 2026-11-09: SSL.com's EV is $349 for a year; Sectigo's
   resellers ask about $280 to $350. Multi-year terms are going away, so expect to buy yearly. For CI the key has to
   be in a cloud HSM rather than on a USB token: SSL.com's eSigner adds about $15 a month and works from GitHub
   Actions; DigiCert's KeyLocker is the equivalent there.
3. **Register in Partner Center** for the hardware program, with the EV certificate. Microsoft's pages give no fee.
4. **Attestation signing for each driver release.** Put the driver packages in a CAB, sign it with the EV
   certificate, submit it, and download the package Microsoft signed. No hardware tests. It can run from CI through
   the Hardware Dashboard API; Microsoft's SDCM tool on GitHub wraps it. Limits:
   - Attestation-signed drivers load on Windows 10 and 11 desktop only. **Windows Server 2016 and later don't load
     them**; a host on Windows Server would need the full route below.
   - They can't be published through Windows Update, so the host's installer installs them itself.
5. **WHQL, only if needed later.** Passing the Hardware Lab Kit tests on real machines and submitting the results
   signs a driver for every Windows version, Server included, and allows Windows Update. Much more work; skip it
   unless Windows Server hosts turn out to matter.

**Order, when phase 6 comes:**

1. Settle whether InspireNXE is, or becomes, a registered organization. Nothing below happens without it.
2. Test the IddCx driver with an ordinary signature, as above. It may not need to wait.
3. Ship gamepads through `InputInjector` or HID in the meantime, and say that XInput-only games won't see them.
4. Buy the EV certificate with cloud signing, register, and attest the gamepad and audio drivers from CI.
5. Keep the drivers in the host's installer only. The client and Orrery Play stay driver-free and portable.

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
- Driver code signing requirements: https://learn.microsoft.com/windows-hardware/drivers/dashboard/code-signing-reqs
- Registering for the Hardware Developer Program: https://learn.microsoft.com/windows-hardware/drivers/dashboard/hardware-program-register
- Driver signing offerings (which Windows versions load what): https://learn.microsoft.com/windows-hardware/drivers/dashboard/driver-signing-offerings
- Kernel-mode code signing policy: https://learn.microsoft.com/windows-hardware/drivers/install/kernel-mode-code-signing-policy--windows-vista-and-later-
- Attestation signing: https://learn.microsoft.com/windows-hardware/drivers/dashboard/code-signing-attestation
- SDCM, Microsoft's Hardware Dashboard API tool: https://github.com/Microsoft/SDCM
- UMDF drivers and signing (archived Microsoft blog): https://learn.microsoft.com/archive/blogs/peterwie/do-umdf-drivers-require-signing
- SSL.com EV code signing: https://www.ssl.com/products/software-integrity/code-signing/ev/
- SSL.com eSigner: https://www.ssl.com/products/software-integrity/signing-service/
- Virtual Display Driver: https://github.com/VirtualDrivers/Virtual-Display-Driver
- Sunshine on Windows: https://docs.lizardbyte.dev/projects/sunshine/latest/md_docs_2getting__started.html
- Sunshine configuration (gamepad and audio options): https://docs.lizardbyte.dev/projects/sunshine/latest/md_docs_2configuration.html
- Apollo: https://github.com/ClassicOldSong/Apollo
- SudoVDA: https://github.com/SudoMaker/SudoVDA
- ViGEmBus: https://github.com/nefarius/ViGEmBus
