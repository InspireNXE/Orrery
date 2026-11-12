# Security and correctness findings, round 2026-11

A pass over what landed after round 2026-10 (from `a675c27` to `a80e35d`, 94 commits): web tabs, session recording,
RDP clipboard images and files, sound and shared folders, serial, `.rdp` import, the Ctrl+K palette, the panes, the
password rules, the recording player rework and the CI build. Six finders, one per surface: web tabs, RDP channels
(this repo and the IronRDP fork's drive backend), recording, import and secrets, supply chain, and a correctness pass
over the latest UI work. Line numbers are at `a80e35d`.

This round was run lighter than 2026-10, at the owner's request to keep it economical: no separate verifier agents.
Each finding below that's marked Fixed was checked against the code before fixing, and most have a regression
test. The ones left Open were read but not proven by a failing input.

Counts: 9 Medium, 20 Low, and 7 correctness bugs. 26 fixed, 10 open (S-4, S-13, S-20 and S-22 were built after the
round, at the owner's word).

---

## Medium

### S-1: "Open in the default browser" hands any scheme a page navigated to, to the shell
Status: Fixed. `OpenInBrowser` takes only http and https.
Where: src/Orrery.App/Sessions/WebPageView.cs (Address set on every navigation); src/Orrery.App/MainWindow.axaml.cs `OpenInBrowser`
Failure: A page sets `location = 'search-ms:…\\attacker\share'` (or any registered handler); the user clicks the
toolbar's browser button -> the shell launches that handler, or sends an NTLM hash to the share.
Test: none (the button needs a live WebView2).

### S-2: An imported file turns on drive sharing without saying so
Status: Fixed. The import preview and report name every connection that shares drives or folders (`ImportReview`).
Where: src/Orrery.Core/Import/RdpFileImporter.cs, MRemoteNgImporter.cs, OrreryImporter.cs; src/Orrery.App/MainWindow.axaml.cs `ImportAsync`
Failure: A `.rdp` with `drivestoredirect:s:*`, an mRemoteNG file with `RedirectDiskDrives`, or an Orrery export with
shared folders names an attacker's host -> after import and connect, that server reads and writes every local drive,
or the home folder off Windows, and nothing said so.
Test: `ImportReviewTests`. The settings are still imported as written, since they're usually the user's own.

### S-3: An Orrery import keeps remote port forwards, and listeners and SOCKS proxies beyond loopback, silently
Status: Fixed, as S-2: named in the preview.
Where: src/Orrery.Core/Import/OrreryImporter.cs; src/Orrery.Core/Connections/PortForward.cs
Failure: A shared export with `R 2222:10.0.0.5:22` or a SOCKS proxy on `0.0.0.0` -> the remote server, or the LAN,
reaches into the user's network once they connect.
Test: `ImportReviewTests.Remote_forwards_and_listeners_beyond_this_computer_are_named_but_loopback_ones_arent`.

### S-4: Jump-host tunnels and forwards on loopback accept any local user
Status: Fixed after the round. Each forwarded port takes one connection, the one the session or the next hop makes
straight after opening it, and refuses and logs the rest (`SshTunnel.AcceptOnlyOne`). Every dial opens its own
forward, so reconnects and RDP redirections still work. A local user who gets in first makes the session fail
visibly instead of riding it unseen.
Where: src/Orrery.Protocols.Ssh/SshTunnel.cs `Forward`; SshSession.cs
Test: `SshTunnelTests` (a second connection to a forward is refused).
Failure: On a shared machine (an RDS host, a multi-user Linux box), user B connects to user A's ephemeral loopback
port while A's RDP or VNC goes through a jump host -> B reaches the internal host over A's SSH session.

### S-5: A server's PNG on the clipboard is decoded at whatever size its header claims
Status: Fixed. The IHDR size is checked against `RdpDib.MaxPixels` before anything decodes it.
Where: src/Orrery.Protocols.Rdp/RdpClipboardBridge.cs (Kind.Png); RdpDib.cs `PngWithinLimit`
Failure: A small PNG declaring 20000x20000 -> a multi-gigabyte decode on the UI thread.
Test: `RdpClipboardFilesTests.Leaves_out_a_png_whose_header_claims_too_many_pixels_or_that_isnt_a_png`.

### S-6: File sizes near 2^63 wrap the 1 GB download total around
Status: Fixed. Sizes are clamped before summing and compared before adding.
Where: src/Orrery.Protocols.Rdp/RdpClipboardBridge.cs `OnRemoteFilesAsync`, the size answer in the download loop
Failure: Two size answers of 1 and 0xFFFF…FF -> the total goes negative, the cap passes, and the client writes until
the disk fills. Two listed sizes of `long.MaxValue` threw an OverflowException that ended clipboard sharing.
Test: `RdpClipboardFilesTests.Refuses_sizes_that_would_wrap_the_total_around`.

### S-7: Locking the vault leaves the recording player open, with its key, and the names in the list
Status: Fixed. Both locks (by hand and on idle) close the player, which zeroes the key, and refresh the list.
Where: src/Orrery.App/MainWindow.axaml.cs `ShowVaultLockedAsync`
Failure: Play a recording, walk away; the vault locks on idle -> anyone at the machine keeps watching, seeking and
exporting.
Test: none (headless lock-and-play test not written).

### S-8: The recordings public key in settings is trusted without checking it against the store
Status: Fixed for the store-open case: whenever a recording starts with the store open, the saved key is compared
with the one the private key gives, and replaced if they differ. While locked the saved key is still used, as the
design requires.
Where: src/Orrery.App/Recording/RecordingService.cs `PublicKeyAsync`
Failure: The vault is recreated (lost key) while settings keep the old public key -> every new recording is sealed
to a key nobody holds. Or a changed `settings.json` seals recordings to someone else's key.

### S-9: Playing a large terminal recording holds every event and builds the search text on the UI thread
Status: Open (a rework of how the player keeps terminal events).
Where: src/Orrery.App/Recording/PlayerView.cs `ReadTimeline`, `BuildText`
Failure: A host streams output for a long session; the recording approaches its 1 GB cap -> opening it takes about
ten times that in memory, or freezes the window.

## Low

### S-10: A page's new-window request opens a tab at any scheme
Status: Fixed. Only http and https open a tab (`WebPageView.OpenNewTab`).
Failure: `window.open('file:///…')` (macOS, Linux, or Windows windowed) -> a tab whose navigation the engine starts
itself, past the rule that pages can't open file: URLs.

### S-11: A page can open tabs without a click, without limit
Status: Fixed. At most 5 tabs a page opens in any 10 seconds; more are dropped. Also reported by the correctness pass.
Failure: A loop of `window.open` or `orrery-tab:` navigations -> hundreds of WebView2 tabs, until memory runs out.

### S-12: A management URL may carry user details, which hide where it goes
Status: Fixed. `ManagementUrl.TryNormalize` refuses any with user info, as `WebAddress` already did.
Test: `ManagementUrlTests` (two new cases).

### S-13: The web profile isn't covered by Move data, and there's no way to clear it
Status: Fixed after the round. Move data still doesn't copy the web folder (sign-ins don't move, which its
confirmation now says), but deleting the old copy removes it. Settings > Web pages has "Clear web data…", which deletes
the engines' folders at once, or at the next start if a page engine has run since Orrery started. Only the folders the
engines write are removed, and links aren't followed (`WebData`).
Test: `WebDataTests`; `DataMoverTests.Web_data_isnt_copied_and_is_deleted_with_the_old_copy_but_not_what_else_is_in_its_folder`.
Failure: Moving from a portable folder leaves the admin consoles' cookies and cache behind in the old folder.

### S-14: The Linux web view forces GDK's X11 backend
Status: Open. Orrery's window is already an XWayland one (README); the web view has to match it until Avalonia's
Wayland backend can host it.

### S-15: WebView2's downloads, external-protocol launches and permission prompts use the engine's defaults
Status: Open (needs a run on a real machine to see what the offscreen mode shows).

### S-16: The clipboard download folder sat under a shared `/tmp/Orrery/Clipboard`
Status: Fixed. Off Windows each download gets its own `mkdtemp` folder (0700, named as it's made).
Failure: Another local user creates the parent first, then swaps a folder for a symlink into `~/.ssh`.

### S-17: A file name the file system can't take ended clipboard sharing for the session
Status: Fixed. Such a file is left out with a log line, and a folder that can't be made refuses the copy.
Failure: 90 CJK characters on Linux (over 255 bytes) -> an IOException out of the bridge loop.

### S-18: Files offered earlier stay readable after the clipboard changes
Status: Fixed. Announcing anything but files clears the offer; locks the server holds still serve what they locked.
Test: `RdpClipboardFilesTests.Stops_serving_offered_files_once_the_clipboard_holds_something_else`.

### S-19: An Orrery file can claim an `.rdp` file's source id
Status: Fixed. Imported `rdpfile:` ids are replaced with the import's own.
Failure: A planted entry carries `rdpfile:C:\Users\Public\Desktop\Corp.rdp` -> opening that `.rdp` later connects
to the planted host.
Test: `ImportReviewTests.An_orrery_file_cant_claim_an_rdp_files_source`.

### S-20: Host checks probe every host in an imported file
Status: Fixed after the round. Host checks are now a setting a folder or connection can turn off ("Check whether the
host is up", inherited like the rest). When checks are on and a file names hosts, the import asks once whether to
check them; "Don't check" turns them off where the import lands and clears any choice the file made below that.
Test: `HostStatusTests.A_folder_with_host_checks_off_stops_them_for_everything_under_it_unless_one_turns_them_back_on`.

### S-21: A copied password isn't cleared if Orrery closes within 30 seconds, and has no clipboard-manager hint off Windows
Status: Open.

### S-22: mRemoteNG exports with passwords use 10,000 PBKDF2 rounds
Status: Fixed after the round, at the owner's word: 50,000 rounds, the most mRemoteNG offers itself. A file with
hundreds of passwords opens a few seconds slower. The export dialog says mRemoteNG's protection is weaker against
guessing than an Orrery export's.
Test: `SecretExportTests` (the file declares `KdfIterations="50000"`).

### S-23: A `.rdp` file's `selectedmonitors` text went into the warning unshortened
Status: Fixed. The warning lists the numbers read, at most 16.

### S-24: The ffmpeg path in settings is run on export without a check
Status: Open. Changing `settings.json` takes the same user's write access, which the profile doesn't count as a
boundary, except for a data folder open to others, which round 2026-10 already warns about.

### S-25: A crafted recording's first size, or a far-off event time, sizes the export
Status: Fixed for the size: `FirstSize` takes only sides up to `ScreenFrames.MaxSide`. Open for event times: a file
stamped weeks long exports as weeks of frames, which Cancel stops.

### S-26: A long quiet stretch between sounds became one huge silence array
Status: Fixed. Silence longer than a second is laid down as frames go out.

### S-27: The recordings folder and files didn't use the owner-only helpers
Status: Fixed. `PrivateStorage.CreateDirectory` for the folder and `PrivateStorage.Private` for each file.

### S-28: AVI export parts overwrote existing files
Status: Fixed. Parts after the first are created new, and an existing one stops the export with its message.

### S-29: The CI test step restored without locked mode
Status: Fixed. The workflow sets `OrreryLockedRestore` for every step.

## Correctness

### B-1: Pane widths saved on a big screen could leave the sessions no room
Status: Fixed. Pinned panes narrow in proportion to leave the sessions 360 pixels; the saved widths are kept.

### B-2: The palette's "Wake" woke the selection, not the connection named, under a tree filter
Status: Fixed. It wakes that connection.

### B-3: Windows without the WebView2 runtime isn't detected
Status: Open. `EngineAvailable` assumes every Windows has it; Windows 10 may not.

### B-4: A web recording's frames decode out of order on a busy pool
Status: Open.

### B-5: Loop restart shows the old picture and pointer until the first key frame
Status: Open.

### B-6: A recording that can't be reopened for frames crashed the player
Status: Fixed. The second open has the same handling as the first.

### B-7: The password rules rejected long passwords made of symbols as "a common password"
Status: Fixed. Symbols count as much as letters; the shifted number row counts as a keyboard run.
Test: `PasswordRulesTests.A_long_password_mostly_of_symbols_isnt_taken_for_a_pattern`.

## Checked and found safe

Recording crypto (XChaCha20-Poly1305 per chunk, authenticated header, no nonce reuse, splicing and truncation
detected), reader limits, retention (top-level `.orrec` only, no links followed), the ffmpeg pipe (current-user
named pipe; a 0600 FIFO in a 0700 folder), server file names on the clipboard (`..`, separators, `:` and NUL
refused, with a final containment check), the fork's drive backend (`openat2` with no symlinks or mount crossing),
CF_DIB limits, sound queues, the `.rdp` importer's refusals (alternate shell, RemoteApp, gateway, `password 51:b`),
the sealed Orrery export, biometric unlock records, workflow actions pinned by SHA with read-only tokens, the IronRDP
package hash check and source mapping. No vulnerable packages (`dotnet list package --vulnerable`).
