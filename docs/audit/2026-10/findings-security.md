# Security findings, round 2026-10

Verified findings from the security pass over `main` at `58e9c72`. A finder read each trust boundary in the
profile. A separate verifier then tried to disprove every claim, and most verifiers built a scratch program to
construct the failing input. Line numbers are at `58e9c72`. Claims the verifiers disproved are in
`rejected-security.md`.

Counts: 1 Critical, 4 High, 15 Medium, 14 Low (34). Of those, 31 are CONFIRMED and 3 are PLAUSIBLE.

## Cross-cutting: nothing catches an unhandled exception

`Program.cs` and `App.axaml.cs` subscribe to neither `Dispatcher.UIThread.UnhandledException` nor
`AppDomain.UnhandledException` nor `TaskScheduler.UnobservedTaskException`. Avalonia 11.3.22 rethrows from a
posted operation when nothing marks it handled, so an exception that escapes an `async void` handler ends the
process, along with every open session. S-10, S-11, S-15, S-16, S-17, S-18 and S-31 each turn a contained fault
into a full crash because of this. Each finding needs its own fix. A dispatcher handler that logs and shows an
error is still worth adding as a backstop, but it isn't a substitute for those fixes.

---

### S-1: A VNC server's desktop size overflows the framebuffer allocation, and the library then writes server pixels past the end of the buffer
Severity: Critical      Verdict: CONFIRMED
Where: src/Orrery.Protocols.Vnc/VncSession.cs:145-165 (allocation at 153), 219-237 (`FrameReference.Size`)
Failure: A VNC server (or a machine in the middle, since RFB isn't authenticated) sends a desktop size of 32768x32768,
then a Raw rectangle -> `AllocZeroed` gets about zero bytes while the buffer is treated as 4 GiB, and the server's
pixel data is written over the native heap. The finder saw the process die with 0xC0000005 against a stand-in server.
This is memory corruption with bytes the attacker chooses.
Evidence: `size.Width * size.Height * BytesPerPixel` is `int` arithmetic, so 32768*32768*4 wraps to 0 before the
`(nuint)` cast. Other sizes wrap to a small positive number. `_bufferWidth`/`_bufferHeight` still get the full size.
The library reads width and height as raw u16 with no cap (`RfbInitializer.GetFramebufferSize`). Its only guard,
`FramebufferUpdateMessageType` comparing `framebufferReference.Size` with the remote size, passes because our
`FrameReference.Size` returns the size it was given. `FramebufferCursor` computes its stride from that size, not
from our allocation.
Rule: none
Fix direction: Reject a desktop size above a fixed cap (the RDP resize clamp of 8192 per side is a reasonable
ceiling) before freeing or allocating anything, and compute the byte count in `long` with `checked`.

### S-2: The VNC library offers 13 security types, and several of them send the saved username and full password in clear text to whoever asks
Severity: High      Verdict: CONFIRMED
Where: src/Orrery.Protocols.Vnc/VncSession.cs:88, 92, 474-477 (`ClosableImplementation : DefaultImplementation`),
491-500 (`Authenticator`); the credential comes from src/Orrery.App/MainWindow.axaml.cs:891-904, 938, 993-995
Failure: A rogue VNC server, or a machine in the middle rewriting the unauthenticated security-type list, offers
type 24 ("IntegratedSSH") -> Orrery sends `alice` and the whole password in plain UTF-8, then shows the session as
Connected. The accepted risk is classic VNC auth, an 8-character DES challenge response, and this goes well
beyond it. The password is often a vault credential shared with other hosts.
Evidence: Community.MarcusW.VncClient 2.0.8 is a single-maintainer fork (karbonbaron, commit 23b030a). Its
`DefaultImplementation` registers VeNCrypt (priority 100), IntegratedSSH (95), SecureTunnel (90), TLS (80),
RA2 (70), SASL (60), RA2ne (50), Tight (40), Ultra (30), XVP (20), MD5 (15), VncAuth (10) and None (1). Upstream
registers only None and VncAuth. The handshaker takes the highest-priority type the server offers, and under RFB 3.3
the server just names one. Our `Authenticator` gives username and password to any type. The verifier drove
Orrery's own `VncSession.Start` against a stand-in server and captured the bytes:
- IntegratedSSH is a stub: a fixed fake KEXINIT, then the credentials in clear, while `IsEncrypted` reports true.
- VeNCrypt subtype 256 (Plain) sends the password in clear.
- SASL falls back to PLAIN for any mechanism other than CRAM-MD5.
- Tight auth 16 and 129 send the password in clear.
- TLS, SecureTunnel and VeNCrypt TLS accept any certificate. A self-signed `CN=attacker.invalid` was accepted and
  the TLS endpoint read the password.
- RA2 encrypts to an RSA key that is never checked.
Rule: none (accepted risks cover classic VNC auth only)
Fix direction: Pass a security-types builder to the three-argument `DefaultImplementation` constructor that returns
only None and VncAuthentication, and make `Authenticator` refuse any other type. This also takes the unvalidated TLS
types out of play.

### S-3: "Delete old copy" after Move data deletes every file under the old folder, follows junctions, and the move check misses drive roots and aliased paths
Severity: High      Verdict: CONFIRMED
Where: src/Orrery.Core/Storage/DataMover.cs:12, 15-35, 49-61, 72-95 (`DeleteOldCopy`), 99-104 (`DataFiles`), 134-137
(`IsInside`); src/Orrery.App/MainWindow.axaml.cs:214-229, 1669-1686; src/Orrery.App/Settings/SettingsView.cs:149-193;
src/Orrery.App/Storage/PortableSetupWindow.cs:100-144; src/Orrery.Core/Storage/DataLocationResolver.cs:29-40;
src/Orrery.Core/Storage/DataLocation.cs:46
Failure: No attacker is needed. The user's own move and delete removes the wrong files. The verifier ran each case in
scratch folders:
- A portable copy whose custom folder holds other files (allowed: the custom choice has no emptiness check, and
  design doc §2.2 says an existing folder is reused). The user's `Thesis.docx`, `Photos\` and `OtherApp\` were deleted
  from the old folder after being copied.
- The installed default folder contains `Portable\` when a portable copy chose "user profile". Moving the installed
  copy and deleting the old one removed that portable copy's data folder.
- A junction inside the data folder (no privilege needed to create one). The files of the junction's target outside
  the data folder were deleted.
- A move from a root (`E:\` to `E:\Backup`) passes `CheckTarget`, because `IsInside` tests
  `StartsWith(folder + separator)` and a root already ends in a separator.
- A target that is inside the source through a junction alias. The copy recursed into itself, and with no lock held
  "Delete old copy" deleted the new data folder entirely. On NTFS the restarted copy's `.lock` sorts first and stops
  the delete; on FAT/exFAT the order isn't guaranteed.
Evidence: `DataFiles` enumerates every file recursively and skips only the top-level `.lock` and
`data-location.json`. `DeleteOldCopy` deletes each one, then every directory left empty. The doc comment at 72-75
promises files the user added stay, and nothing implements that. `CheckTarget` compares `Path.GetFullPath` strings
and resolves no links. The only test (`DataMoverTests.cs:66-76`) covers the pointer file.
Rule: docs/design-storage-vault-import.md §2.7; DataMover.cs:72-75; the profile's never-weaken entry for `DataMoverTests`
Fix direction: Copy and delete from an allowlist of Orrery's own names, never recurse into `Portable\`, skip reparse
points and symlinks, and use a containment check that works for roots and resolves links. Add a `DataMoverTests`
case for each scenario above. Record the pending old path in the new data folder instead of trusting `--moved-from`.

### S-4: On Windows, a data folder outside the user profile inherits an ACL that lets any other local user change it, including planting trust pins
Severity: High      Verdict: CONFIRMED
Where: src/Orrery.Core/Storage/DataFolderLock.cs:16-20; AtomicFile.cs:11-21; DataLocationResolver.cs:34-36, 81-84;
DataMover.cs:53-54; src/Orrery.App/Storage/PortableSetupWindow.cs:41, 73, 128-129; src/Orrery.App/Settings/SettingsView.cs:151-177;
src/Orrery.Core/Security/JsonCertificateTrustStore.cs:40-53
Failure: The data folder sits under `C:\`, on a second NTFS or ReFS drive, or on exFAT. Any of these happens through
Move data or a portable custom folder. Another user who logs into the same machine then edits `connections.json` to
point a saved connection at their host, and adds a pin for their key to `known-hosts.json` or
`trusted-certificates.json` -> the next connect sends the saved password to them with no trust prompt. They can also
read `vault.frv` (for an offline guess), `credentials.json` (names, usernames, notes) and the connection list.
Evidence: No code in the repo sets or checks an ACL (no `SetAccessControl`, `FileSecurity` or `DirectorySecurity`).
On this machine `icacls C:\` shows `Authenticated Users:(OI)(CI)(IO)(M)`, so new subfolders are modifiable by every
logged-in user. `E:\` and `W:\` grant the same, and `D:\` is exFAT with no ACLs. `File.Copy` takes the target's
inherited ACL, so moving out of `%APPDATA%` loosens access silently. `%APPDATA%\Orrery` itself is fine.
The trust stores are plain JSON with no integrity check. For portable "with the app", a folder other users can write
also lets them replace `Orrery.exe`, so only a warning helps there.
Rule: none (the design doc doesn't say who may read or write the data folder)
Fix direction: Give folders Orrery creates a protected ACL for the current user, SYSTEM and Administrators. When the
user picks a folder, check whether other users can write to it and warn, both at setup and in `CheckTarget`. Use a
unique temp name with `FileMode.CreateNew` in `AtomicFile` (the predictable `.tmp` is only exploitable once the folder
is writable by others).

### S-5: SSH.NET keeps every byte an SSH server sends in memory, because Orrery never reads the ShellStream
Severity: Medium      Verdict: CONFIRMED
Where: src/Orrery.Protocols.Ssh/SshSession.cs:90-93
Failure: Any SSH host, including a benign one over a long session, sends a lot of output (`yes`, a runaway log) ->
memory grows by every byte received, up to about 2 GiB per session (about 3 GiB peak with doubling). After that the
terminal stops showing output without a message. Several sessions, or a small machine, run out of memory first.
Evidence: SSH.NET 2026.0.0 copies each packet into `ShellStream._readBuffer` before raising `DataReceived`. Only
`Read`, `ReadLine` and `Expect` drain it, and Orrery calls none of them. The channel window is re-opened whether or not
anyone reads, so there is no backpressure. Two verifiers each pushed 256 MiB through a real `ShellStream` with
only a `DataReceived` subscriber, and `Length` tracked every byte.
Rule: none
Fix direction: Read from the stream (and feed the terminal from those reads) instead of using `DataReceived`, or drain
and discard what `DataReceived` already delivered. Add a test that feeds several MiB and checks `Length` stays small.

### S-6: An RDP server's desktop size goes unchecked into three buffer allocations, overflows int, and can crash the app from the UI thread
Severity: Medium      Verdict: CONFIRMED
Where: src/Orrery.Protocols.Rdp/RdpSession.cs:89-90, 229-268 (`ResetImage`, allocation at 266); src/Orrery.App/Sessions/RemoteFrameView.cs:224-245
(`WriteableBitmap` at 234)
Failure: An RDP server that got past the certificate check announces a huge desktop, at connect or on any
reactivation -> about three 2 GB allocations per announcement (native `DecodedImage`, `_frame`, the UI bitmap), or an
overflowed byte count. The UI-thread bitmap allocation then throws with no catch and the process ends.
Evidence: `DesktopSize` is ushort x ushort and nothing caps it. The 200-8192 clamp in `RequestResize` (366-368) covers
only our own request. Run in a scratch program: 65535^2 gives `OverflowException` and 32768^2 gives a zero-length
`_frame`. Avalonia's `WriteableBitmap` sizes its blob with Skia's int `BytesSize`, which throws for a non-positive
value or tries a huge `AllocHGlobal`. Not run against a live RDP server.
Rule: none
Fix direction: Validate the size before touching any state, using the same 8192 ceiling as `RequestResize`; compute in
`long`; allocate before assigning `Width`/`Height`; and end the session instead of crashing if `Flush` fails.

### S-7: The shipped IronRDP predates three upstream fixes for hostile server input, and one of them is reachable even with clipboard sharing off
Severity: Medium      Verdict: CONFIRMED
Where: Directory.Build.props:17 (`IronRdpVersion` 2026.10.29); src/Orrery.Protocols.Rdp/RdpConnector.cs:29 (drdynvc always attached), 30-33 (cliprdr)
Failure: A trusted-but-hostile or compromised RDP server sends compressed static-channel fragments without end ->
the native reassembly buffer grows without limit. Or it sends an out-of-range desktop size -> a native allocation
failure aborts the process (Rust can't unwind through the FFI).
Evidence: The package's nuspec names commit c3a782ff (2026-10-29). Upstream merged #2084 (reject out-of-range desktop
sizes), #2083 (bound compressed static-channel reassembly by its declared length) and #2096 (RFX geometry wrapping
u16) on 2026-10-30, and #2084's error text isn't in the shipped DLL or .so. #2083's path is reachable through cliprdr
and through the dynamic-channel transport that `RdpConnector` attaches unconditionally. No newer package exists on
NuGet. Even after these fixes upstream still accepts 32766^2 (about 4.3 GB), so S-6's own cap is still needed.
Rule: none
Fix direction: Bump `IronRdpVersion` when a package containing #2083, #2084 and #2096 is published. Until then, S-6's
cap is the only mitigation in our hands.

### S-8: On Linux and macOS every data file is created with umask defaults (0644/0755), and every save resets a tightened mode
Severity: Medium      Verdict: CONFIRMED
Where: src/Orrery.Core/Storage/AtomicFile.cs:11-21; DataFolderLock.cs:16-20; src/Orrery.Core/Connections/ConnectionStore.cs:53, 58, 69;
src/Orrery.Core/Credentials/VaultFileStore.cs:330; src/Orrery.Core/Credentials/KeychainCredentialStore.cs:117;
src/Orrery.Core/Security/JsonCertificateTrustStore.cs:42; src/Orrery.Core/Settings/AppSettings.cs:95;
src/Orrery.Core/Connections/RecentConnections.cs:60; src/Orrery.App/MainWindow.axaml.cs:1587
Failure: Another user on a shared Linux machine (home 0755 with `~/.config` 0755, as on older Debian/Ubuntu and many
servers), or on any machine where the data folder is a portable or custom folder, reads `vault.frv`. Linux falls
back to the vault when there's no Secret Service, which is the headless and shared-server case. They also read
`credentials.json`, `connections.json` and the trust stores. They can attack the vault offline but can't write.
Evidence: Nothing in `src` sets `UnixCreateMode` or `UnixFileMode`. The .NET 10.0.14 linux-x64 CoreLib creates files
0666 and directories 0777 before the umask. `File.Move(overwrite)` is `rename(2)`, so a user's `chmod 600 vault.frv`
is lost on the next save; a folder `chmod 700` survives. macOS installed mode is shielded by `~/Library` being 0700.
Rule: none
Fix direction: In `AtomicFile`, create files 0600 (`FileStreamOptions.UnixCreateMode`) and folders 0700
(`Directory.CreateDirectory(path, UnixFileMode)`). Do the same for backups, the lock file and the import report, and
tighten an existing data folder at start-up.

### S-9: Changing the master password keeps the same data key, so an old copy of the vault plus the old password opens the current one
Severity: Medium      Verdict: CONFIRMED
Where: src/Orrery.Core/Credentials/VaultFileStore.cs:189-254 (unwrap 212-217, re-wrap 226-230, `Save` at 240), 322-336,
381-382; src/Orrery.App/Credentials/ChangeMasterPasswordWindow.cs:46; docs/design-storage-vault-import.md §3.3
Failure: The user suspects the master password leaked, changes it, then rotates the server passwords inside. Someone
who has the old password and an older `vault.frv` (a backup, a synced folder's version history, an old USB image)
unwraps the data key from the old file -> decrypts the current file, including the newly rotated passwords.
Evidence: The verifier built this with the real `VaultFileStore`. It created a vault, copied the file, changed the
password and added a credential with secret `pw-ADDED-AFTER-CHANGE`. The old copy plus the old password gave a DEK
identical to the current one, and that DEK decrypted the current payload. The associated data comes only from the
plaintext header, so nothing secret is needed to rebuild it. `Save` already re-encrypts the payload during the
change, so a fresh DEK costs nothing extra. The UI tells the user "Your saved credentials stay as they are" and says
nothing about old copies.
Rule: §3.3 describes the re-wrap as intended; this finding is about its consequence. Decision for the owner (see the round file).
Fix direction: On a password change, generate a new DEK, re-encrypt the payload with it, dispose the old key, and update §3.3.

### S-10: A wide character in the last column with auto-wrap off writes past the row and kills the Telnet reader; closing the tab then crashes the app
Severity: Medium      Verdict: CONFIRMED
Where: src/Orrery.Core/Terminal/TerminalEmulator.cs:523-533, 547-552 (`Cells[_cx + 1]` at 551), 852;
src/Orrery.Protocols.Telnet/TelnetSession.cs:54-108 (filters 95-107), 202-220 (`await _run` at 210);
src/Orrery.App/MainWindow.axaml.cs:1106, 1147-1169, 1238, 1317-1324, 1338-1342, 1356
Failure: A Telnet host sends `ESC[?7l`, moves to the last column and prints `中` -> `IndexOutOfRangeException` in
`Feed`; the read loop faults while the tab still says Connected. When the user closes, disconnects or reconnects
the tab, `DisconnectAsync` rethrows into an `async void` handler and the whole app exits. On SSH, SSH.NET catches the
exception and drops the rest of that chunk.
Evidence: The wrap branch only runs with auto-wrap on. `ClearWideAt` guards `x >= Columns` but the direct write at
551 doesn't, and the `Columns < 2` check at 535 can never be true. Telnet's catch covers only Socket, IO,
ObjectDisposed and Canceled exceptions. Ran against a loopback Telnet server: `_run` faulted with
IndexOutOfRange, and `DisconnectAsync` and `DisposeAsync` both rethrew it.
Rule: none
Fix direction: Clamp the continuation-cell write at the margin. Catch and contain parser faults in `Feed`, resetting the
parser. Catch everything in the Telnet read loop and fail the session.

### S-11: Growing the terminal leaves lines pulled back from scrollback at the old width, so ordinary output later throws
Severity: Medium      Verdict: CONFIRMED
Where: src/Orrery.Core/Terminal/TerminalEmulator.cs:1020-1022 (screens resized before scrollback), 1045, 1063-1070; failing
sites 566-578, 375-380, 747-754, 697-720; triggers in src/Orrery.App/Sessions/TerminalView.cs:104-118, 170-187
Failure: With scrollback present, the user maximises, goes full screen, collapses a pane or presses Ctrl+-, which
grows rows and columns in one resize. Then `clear` and a line wider than the old width -> `IndexOutOfRange` or
`ArgumentException`. On Telnet that leads to S-10's crash; on SSH the output is garbled. Ordinary hosts trigger it.
Evidence: `ResizeScreen` inserts `_scrollback[^1]` into the screen without resizing it, and the scrollback resize at
1022 runs afterwards on what's left. Run: 80x24 to 120x30 left six rows 80 wide, and `ESC[1;100Hx`, `ESC#8` and DCH
each threw.
Rule: none
Fix direction: Resize each line as it's pulled back (or the scrollback first), and keep `Cells.Length == Columns` as an
invariant with a test.

### S-12: The escape-sequence intermediates buffer has no cap, so a host can grow memory without limit
Severity: Medium      Verdict: CONFIRMED
Where: src/Orrery.Core/Terminal/TerminalEmulator.cs:47, 275-279, 352-354, 407-411, 310-314, 343, 435
Failure: An SSH or Telnet host sends `ESC (` or `ESC [` followed by an endless run of 0x20-0x2F bytes -> memory grows
by about twice the bytes received and stays allocated for the life of the session.
Evidence: Parameters (17, 396) and OSC (18, 290) are capped; `_intermediates` isn't. Run: 200 MiB of spaces after
`ESC (` grew the heap by 401 MiB, and `StringBuilder.Clear` kept the capacity afterwards.
Rule: none
Fix direction: Stop appending after a few intermediates and mark the sequence invalid, as the VT500 parser does.

### S-13: REP (`CSI n b`) turns 9 bytes into 65,535 prints while `Feed` holds the lock every terminal tab renders under
Severity: Medium      Verdict: CONFIRMED
Where: src/Orrery.Core/Terminal/TerminalEmulator.cs:174-192, 390, 481; src/Orrery.App/Sessions/TerminalView.cs:195, 654
Failure: A host streams `x ESC[65535b` at a few KiB/s -> each 64 KiB chunk holds the emulator lock for 4.5-5 s, and the
UI thread's render waits on it, so the whole window stalls for as long as the host keeps sending.
Evidence: The count is capped at 65535 but not tied to the screen size, and `Feed` holds `SyncRoot` for the whole chunk.
Streaming at about 16 KiB/s, a stand-in UI thread got the lock 7 times in 8 s.
Rule: none
Fix direction: Clamp REP to `Columns * Rows`, and feed in bounded slices that release the lock between them.

### S-14: Telnet subnegotiation data accumulates with no cap until `IAC SE`
Severity: Medium      Verdict: CONFIRMED
Where: src/Orrery.Protocols.Telnet/TelnetSession.cs:244 (`_sub`), 271, 281-295
Failure: A Telnet server sends `IAC SB <option>` and then bytes forever without `IAC SE` -> `_sub` grows until the
process runs out of memory.
Evidence: Every byte in the Sub and SubCommand modes is appended. `_sub` is cleared only on the next `SB`.
Rule: none
Fix direction: Cap `_sub` at a few KB (NAWS and TTYPE are all we answer) and reset the parser or close the session past it.

### S-15: A VNC server's clipboard update can crash the app, because the local clipboard write has no catch
Severity: Medium      Verdict: CONFIRMED
Where: src/Orrery.App/Sessions/RemoteFrameView.cs:79-89 (`SetTextAsync` at 86); compare the guarded send path at 388-400
Failure: A VNC server sends ServerCutText repeatedly, including while Orrery is in the background -> when another
process holds the Windows clipboard open, `SetTextAsync` throws, and the exception escapes an `async void` lambda on
the dispatcher and ends the process.
Evidence: The receive handler is `Dispatcher.UIThread.Post(async () => { ... await clipboard.SetTextAsync(text); })`
with no try/catch. The send path catches `COMException` and `InvalidOperationException` for exactly this
contention. The library caps the text at 262144 bytes, so this is a crash, not memory growth.
Rule: none
Fix direction: Wrap the write in the same catch the send path uses.

### S-16: A faulted RDP clipboard loop is rethrown when the user closes the tab, and the app exits
Severity: Medium      Verdict: CONFIRMED
Where: src/Orrery.Protocols.Rdp/RdpSession.cs:137-167 (catches at 157 and 161), 169-176, 471-482, 431-451;
src/Orrery.App/MainWindow.axaml.cs:1106, 1238, 1317-1322
Failure: The server drops the connection while the clipboard loop is about to write (its 5 s timeout drive or a local
clipboard change) -> an `IOException` faults `_clipboardLoop`. When the user closes the tab or clicks Disconnect,
`DisconnectAsync` awaits the loop, the exception reaches an `async void` handler, and the process ends.
Evidence: The loop catches only `OperationCanceledException` and `IronRdpException`; the write throws plain
`IOException`. The try/catch at 431-440 covers the graceful-shutdown call but not the awaits at 448-451.
Rule: none
Fix direction: Catch and log everything except cancellation in the loop, and let `DisconnectAsync` tolerate a faulted task.

### S-17: A crafted confCons.xml throws an exception that `ImportAsync` doesn't catch, and the app exits
Severity: Medium      Verdict: CONFIRMED
Where: src/Orrery.Core/Import/MRemoteNgImporter.cs:139-148 (the only XmlException guard), 172-179 (inner load), 206-213
(`ToDictionary` at 209); src/Orrery.App/MainWindow.axaml.cs:1504-1529 (filter at 1525), 1487, 380-385, 424-431,
214-229, 1640-1645; src/Orrery.App/Settings/SettingsView.cs:484
Failure: The user imports a file with `<Node Name="n" xmlns:Name="urn:x" .../>` -> `ArgumentException` from the
attribute dictionary. Or the file sets `FullFileEncryption="true"` and `Protected=""`, so the importer uses mRemoteNG's
public default key, and the encrypted body is malformed XML or a DOCTYPE -> an unguarded `XmlException`. Either way
the exception leaves the import and ends the process, losing up to 500 ms of unsaved edits.
Evidence: Attributes are keyed by `LocalName`, and namespace declarations count as attributes. The inner load sits
outside the `try` at 140-148. `ImportAsync` catches only Import, IO, UnauthorizedAccess and
MasterPasswordRequired exceptions, and every caller is `async void`. Each case was reproduced through the real
importer, and a headless replica of the handler ended the process.
Rule: design doc §4.9 (fuzz the parser with malformed input)
Fix direction: Make the importer turn every bad-input failure into `ImportException`. Guard the inner load the same way
as the outer one, read only empty-namespace attributes, and add a final catch in `ImportAsync` that shows the error.

### S-18: Folders nested 29-31 deep make every save throw, and about 1,400 nested containers overflow the stack on import
Severity: Medium      Verdict: CONFIRMED
Where: src/Orrery.Core/Connections/ConnectionStore.cs:18-24 (no `MaxDepth`), 46-54; src/Orrery.App/MainWindow.axaml.cs:176-183,
277-288 (filter at 284), 1554-1570, 725-747; src/Orrery.Core/Import/MRemoteNgImporter.cs:206-213, 257-303 (recursion at 297-300)
Failure: An imported file with 29 nested containers, or a user who makes 31 nested folders by hand, hits
`System.Text.Json`'s default depth of 64 -> `JsonException` from `SaveNow`, which isn't caught, so the app exits from
the import or the save timer. On import, the credentials were already written, so they're left orphaned. A file with
about 1,400 nested containers (74 KB) overflows the stack in `Builder.Add` on the import thread, which can't be caught.
Evidence: Each folder level costs two JSON levels. The real `ConnectionStore.Save` threw at 29 levels under an import
folder and at 30 at the top level. `SaveNow` catches only `IOException` and `UnauthorizedAccessException`. Every depth
that saves also loads, so `connections.json` is never left unreadable; only changes since the last good save are lost.
Rule: design doc §4.9
Fix direction: Set an explicit `MaxDepth` with headroom and catch `JsonException` in `SaveNow`. Cap nesting in the UI
and in the importer (counted iteratively before any recursion), below what the serializer accepts.

### S-19: Import has no limit on node, report-line or credential counts, so a large file blocks the UI for minutes
Severity: Medium      Verdict: CONFIRMED
Where: src/Orrery.Core/Import/MRemoteNgImporter.cs:113, 136; src/Orrery.App/Import/ImportPreviewWindow.cs:42-45, 76, 87-102;
src/Orrery.App/MainWindow.axaml.cs:1534, 1545-1558; src/Orrery.Core/Credentials/KeychainCredentialStore.cs:84-96, 111-117;
src/Orrery.Core/Credentials/VaultFileStore.cs:283-294, 322-336; src/Orrery.Core/Storage/AtomicFile.cs:9-22
Failure: A 61.6 MB file of 1.1 million unsupported-protocol nodes -> the preview blocked the UI thread for about 66 s and
used 10.8 GB (100,000 lines: 6.9 s and 1.15 GB). A file with thousands of distinct passwords -> the credential save runs on
the UI thread and rewrites and fsyncs the whole store per credential: 1,000 took 7.3 s, 3,000 took 27.5 s. Killing the app
partway leaves credentials saved and connections not added.
Evidence: The only limit is `MaxCharactersInDocument` (64M). The preview adds one wrapping `TextBlock` per line to a
non-virtualised `StackPanel`. Both stores' `SaveAsync` complete synchronously, so the `await` loop stays on the UI thread.
Measured with the real preview window under headless Skia and the real stores.
Rule: design doc §4.9 ("Cap the file size")
Fix direction: Cap nodes and report lines and show "and N more" in the preview. Save imported credentials in one batch
off the UI thread, then add the connections.

### S-20: The bracketed-paste guard strips the end marker in one pass, so pasted text can rebuild it and run the rest as typed commands
Severity: Low      Verdict: CONFIRMED
Where: src/Orrery.Core/Terminal/TerminalKeys.cs:110-115; src/Orrery.App/Sessions/TerminalView.cs:547-551, 701-709;
src/Orrery.App/MainWindow.axaml.cs:1289
Failure: The user copies text from a hostile web page ("pastejacking") and pastes it into a shell that has bracketed
paste on -> `ESC[2` + `ESC[201~` + `01~` becomes a real end marker after the strip, and the text after it runs as
typed commands. Run: `\rid\r` arrived as input.
Evidence: `text.Replace("\u001b[201~", "")` runs once. ESC and other C0 controls pass through. The code comment promises
it strips anything that could end the bracket early. xterm drops ESC from pastes by default (from memory; not checked).
Rule: none
Fix direction: Remove ESC (and C0 controls other than tab, CR and LF) from pasted text, or repeat the strip until nothing changes.

### S-21: Copy and Save output keep C1 control characters from the host, which a terminal runs when the saved log is displayed later
Severity: Low      Verdict: CONFIRMED
Where: src/Orrery.Core/Terminal/TerminalEmulator.cs:101-157, 264-268, 1089-1110; src/Orrery.Core/Terminal/TerminalCell.cs:113-128;
src/Orrery.App/Sessions/TerminalView.cs:688-698; src/Orrery.App/MainWindow.axaml.cs:1387-1411
Failure: A host prints UTF-8 U+009B or U+009D sequences -> they land in cells and come out verbatim in "Save output"
and Copy. `cat`-ing the saved file later in a terminal that honours C1 in UTF-8 runs them (clear, rewrite, retitle).
Evidence: In the ground state only `cp < 0x20` and 0x7F are controls; 0x80-0x9F print with width 1. Run: `AllText`
returned U+009B and U+009D intact, written as `C2 9B`. Hidden (SGR 8) text and bidi controls are kept too, but that's
ordinary terminal behaviour.
Rule: none
Fix direction: Drop C1 code points at `Print`, through the existing width-0 early return.

### S-22: Text from an SSH server or agent is fed to the terminal emulator with its escape sequences when a connection fails
Severity: Low      Verdict: CONFIRMED
Where: src/Orrery.Protocols.Ssh/SshSession.cs:80, 82, 113-116, 264-279; src/Orrery.App/Sessions/TerminalView.cs:121-135;
src/Orrery.Protocols.Ssh/Agent/AgentKeySource.cs:43-45
Failure: Any server, or a machine in the middle before key exchange, sends SSH_MSG_DISCONNECT with escape sequences
in its description -> the failed tab shows attacker-drawn text (the verifier drew a fake "Connected." and "Password:").
Keystrokes and terminal replies are dropped because the session is already Failed, so it's spoofing only. An agent key
comment reaches the same sink through S-25.
Evidence: SSH.NET puts the description verbatim into the exception message, and `ShowState` interpolates it into
`"Connection failed: {message}"` and calls `Feed`.
Rule: none
Fix direction: Strip control characters from the message before it reaches `Feed`.

### S-23: SSH.NET's default algorithm lists, which Orrery doesn't narrow, include SHA-1 key exchange, CBC, 3DES and hmac-sha1
Severity: Low      Verdict: CONFIRMED
Where: src/Orrery.Protocols.Ssh/SshSession.cs:61-64
Failure: A legacy server that offers only old algorithms gets a group1-sha1, 3des-cbc or hmac-sha1 connection with no
warning. Strict KEX is used only when the server offers it.
Evidence: The weak entries are listed last, and the signed exchange hash covers KEXINIT, so a man in the middle can't
downgrade a modern server. This is hardening.
Rule: none
Fix direction: Remove group1-sha1, group14-sha1, gex-sha1, 3des-cbc and hmac-sha1 from the lists, and add a per-connection
"allow legacy algorithms" switch.

### S-24: SSH keystrokes are written synchronously on the UI thread, so a server that stops reading freezes the window
Severity: Low      Verdict: CONFIRMED
Where: src/Orrery.Protocols.Ssh/SshSession.cs:194-212; src/Orrery.App/Sessions/TerminalView.cs:73, 392-419, 463-473, 512, 640, 708
Failure: A logged-in server stops granting channel window -> the next keystroke freezes the UI for up to 20 s (about 40 s
if a terminal reply holds the send lock), then the session fails. Traced, not run: a server that stops reading TCP
blocks `socket.Send`, which has no timeout, so the UI blocks indefinitely after a large paste.
Evidence: `Send` calls `shell.Write` and `Flush` on the caller's thread, and SSH.NET waits on the window with
`ConnectionInfo.Timeout`.
Rule: none
Fix direction: Queue writes to a background writer with a bounded queue, and fail the session if it backs up.

### S-25: The OpenSSH agent pipe is trusted on its name alone and opened with full impersonation allowed
Severity: Low      Verdict: CONFIRMED
Where: src/Orrery.Protocols.Ssh/Agent/SshAgentClient.cs:32-53, 115-118, 133-146
Failure: The OpenSSH Authentication Agent service is stopped and disabled, which is the Windows default, so
`\\.\pipe\openssh-ssh-agent` is free. Another user, or a low-integrity process, creates it -> every Orrery with "Use SSH
agent" (the default) talks to the squatter first. The squatter blocks Pageant, returns fake keys, hangs connects (S-27)
and reads the username and session ID from sign requests. With SeImpersonatePrivilege it can also impersonate the user.
It doesn't learn the user's real public keys.
Evidence: `Find` picks the pipe when `File.Exists` is true, before Pageant. The three-argument `NamedPipeClientStream`
sets no `SECURITY_SQOS_PRESENT`, so the server gets SecurityImpersonation (seen in a probe; Identification when the level
is passed). Nothing checks who owns the server. The stock `ssh.exe` uses the same name; the missing SQOS and owner check are ours.
Rule: none
Fix direction: Open with `TokenImpersonationLevel.Identification`, and check that the pipe's server process runs as
SYSTEM or the current user (`GetNamedPipeServerProcessId`) before sending anything.

### S-26: A malformed agent reply or a sign-time error leaves the SSH tab stuck on "Connecting…"
Severity: Low      Verdict: CONFIRMED
Where: src/Orrery.Protocols.Ssh/Agent/SshAgentClient.cs:210-217 (213); src/Orrery.Protocols.Ssh/SshSession.cs:53, 113, 187;
src/Orrery.Protocols.Ssh/Agent/PageantTransport.cs:46-59; src/Orrery.App/MainWindow.axaml.cs:1016-1038
Failure: A hostile agent (S-25 or S-28) answers a sign request so that `Sign` throws `Win32Exception` or
`UnauthorizedAccessException`. Or it sends a string length near `int.MaxValue` on the second listing -> the exception
escapes `Run`'s filter, the discarded task faults, and the tab sits at "Connecting…" until the user disconnects. The
length case fails the session instead of falling back to another login method.
Evidence: `_position + length > data.Length` wraps (`ArgumentOutOfRangeException` at the probed boundaries). `Run`'s filter
at 113 doesn't list `Win32Exception` or `UnauthorizedAccessException`.
Rule: none
Fix direction: Compare by subtraction in `WireReader`, and wrap agent failures in `IOException` inside the sign path.

### S-27: Agent calls have no timeout, and a hung Pageant window blocks every later Pageant call through a static lock
Severity: Low      Verdict: CONFIRMED
Where: src/Orrery.Protocols.Ssh/Agent/PageantTransport.cs:25-38, 69; src/Orrery.Protocols.Ssh/Agent/SshAgentClient.cs:138, 151-169;
src/Orrery.Protocols.Ssh/SshSession.cs:61, 142, 176-192
Failure: A fake agent (a pipe squatter from S-25, or a low-integrity window named "Pageant" on the user's desktop)
accepts and never answers -> the connect never proceeds, with no message. For Pageant, every later agent connection in
the app hangs too.
Evidence: `SendMessage(WM_COPYDATA)` runs inside `lock (Gate)` with no timeout; `ReadExactly` has none; the listing runs
before SSH.NET's connect timeout applies. Probes showed a low-integrity window blocking `SendMessage` and a silent pipe
holding `ListIdentities`. `FindWindow` doesn't check who owns the window.
Rule: none
Fix direction: Use `SendMessageTimeout` and read timeouts, and check the Pageant window's owning process before sending.

### S-28: A low-integrity process can pre-create Orrery's Pageant shared-memory block and read the sign request
Severity: High (lower-integrity processes are in scope, owner decision 2026-10-30)      Verdict: PLAUSIBLE
Where: src/Orrery.Protocols.Ssh/Agent/PageantTransport.cs:44-51, 63, 69, 80-88
Failure: A low-integrity process of the same user (a sandboxed browser renderer, for example) creates
`PageantRequest{threadid:x8}` before Orrery does -> Orrery writes its sign request into the attacker's section. If
Pageant then accepts that section, the attacker could swap in its own request during the blocking `SendMessage` and read
the signature back. That would give a sandboxed process signatures from the user's keys.
Evidence: Probe: a low-integrity child created a NULL-DACL section under a test name. `CreateFileMapping` with
Orrery's descriptor returned the existing section (last error 183, which Orrery doesn't check), and the child read
"SIGN REQUEST FROM ORRERY". Thread IDs are enumerable, so the name is predictable. Not tested: whether Pageant accepts
a section owned by the same user SID at a lower integrity level (Pageant isn't installed here).
Rule: none
Fix direction: Fail on `ERROR_ALREADY_EXISTS`, and use a random section name. Modern Pageant (0.75+) also listens on a
per-user named pipe, which would avoid shared memory entirely; check that before relying on it.

### S-29: During CredSSP the client buffers a server's TsRequest with no size cap and no timeout
Severity: Low      Verdict: PLAUSIBLE
Where: src/Orrery.Protocols.Rdp/RdpConnector.cs:140-153 (146)
Failure: A server that passed the certificate check answers with a TsRequest that uses a 3- or 4-byte DER length and
keeps sending -> the client buffers it, with roughly quadratic CPU, until the user closes the tab.
Evidence: IronRDP's PDU hint returns "size unknown" for those length forms, and `ReadByHint` appends until it has a full
PDU, with only the session token to stop it. Growth is limited to what the server sends. Not confirmed whether the
native hint caps the size.
Rule: none
Fix direction: Cap the CredSSP PDU size and put a timeout around `PerformCredsspAsync`.

### S-30: When a session connects it takes keyboard focus even if the user is typing a password in the credential editor
Severity: Low      Verdict: PLAUSIBLE
Where: src/Orrery.App/Sessions/RemoteFrameView.cs:164-183 (`Focus()` at 180); src/Orrery.App/Sessions/TerminalView.cs:137-140;
src/Orrery.App/MainWindow.axaml.cs:482 (credential editor in the right pane); src/Orrery.App/Credentials/CredentialsView.cs:37, 39
Failure: The user opens a connection to a slow host, switches to the Credentials panel and types a password into the
editor. The session finishes connecting and moves focus -> the rest of the password goes to the remote host. A hostile
server controls the timing.
Evidence: Both session views call `Focus()` unconditionally on Connected. The verifier rejected this because every
credential prompt is modal (`ConnectPromptWindow`, `VaultUnlockWindow`, `ChangeMasterPasswordWindow`). That's true,
but the credential editor isn't a dialog: `_propertiesHost.Child = _credentialsView.Editor` (MainWindow.axaml.cs:482)
hosts it in the main window beside the session, with `PasswordChar` boxes. Not run in the app.
Rule: none
Fix direction: Move focus to the session on connect only when focus is on the session view or on nothing.

### S-31: An edited vault header crashes the app at unlock, and its KDF cost is taken from the file without bounds
Severity: Low      Verdict: CONFIRMED
Where: src/Orrery.Core/Credentials/VaultFileStore.cs:129-181, 360-379; src/Orrery.App/Credentials/VaultUnlockWindow.cs:41, 124-133;
src/Orrery.App/Credentials/ChangeMasterPasswordWindow.cs:33, 95-104
Failure: Someone who can write `vault.frv` (a shared or synced folder, a tampered stick), or an ordinary sync conflict,
leaves a missing block, bad JSON, bad base64 or out-of-range KDF parameters -> unlock throws `JsonException`,
`NullReferenceException`, `FormatException` or `ArgumentException`, none of which the dialogs catch, and the app exits.
`iterations` set to `int.MaxValue` keeps a core busy for months after Cancel.
Evidence: `Open` checks only the format, version and algorithm strings, then hands the KDF parameters to Argon2 before
the associated-data check. A headless run of the real `VaultUnlockWindow` showed each exception reaching the
dispatcher's unhandled path. The associated data still blocks any KDF downgrade.
Rule: none
Fix direction: Validate the header up front (required blocks, base64 lengths, KDF bounds) and map every failure to
`InvalidDataException`, which the dialogs already handle.

### S-32: After auto-lock, the inspector's "Saved credential" dropdown still lists every credential's name and DOMAIN\user
Severity: Low      Verdict: CONFIRMED
Where: src/Orrery.App/MainWindow.axaml.cs:165-172 (auto-lock handler) vs 1598-1605 (manual lock);
src/Orrery.App/Credentials/CredentialPicker.cs:37-75; src/Orrery.App/Connections/PropertiesPanel.cs:174-179
Failure: Someone at the machine after the vault auto-locks opens the dropdown on the selected connection -> it shows
every credential, e.g. "Prod domain admin (CORP\admin)". Those names are encrypted in the vault file.
Evidence: The manual lock refreshes the inspector (`_properties.ShowAsync` at 1604); the auto-lock handler only updates
the button and the side list. A headless run with the real controls showed the picker still populated after the
30 s auto-lock. `ConnectPromptWindow.cs:73-77` has the same staleness if auto-lock fires while it's open.
Rule: none (CredentialsView.cs:194 states the intent: "nothing from a locked store stays visible")
Fix direction: Refresh the inspector from the auto-lock handler the same way the manual lock does.

### S-33: Import takes `KdfIterations` from the file with no upper bound, and credential naming is cubic
Severity: Low      Verdict: CONFIRMED
Where: src/Orrery.Core/Import/MRemoteNgImporter.cs:169-170, 184-199, 342-354, 372-382; src/Orrery.Core/Import/MRemoteNgCrypto.cs:35-78, 96-101
Failure: A file sets `KdfIterations="2147483647"` and adds junk password values of 48 bytes or more -> about 24 minutes of
PBKDF2 per value, on a background thread with no cancel. Thousands of passwords for one username make `CredentialName`
cubic (3,000 took 18.7 s). The UI stays usable, and closing the app stops it.
Evidence: Line 169 accepts any positive int. `TryDecrypt` derives the key before the tag check, and nothing is cached.
Rule: design doc §4.9
Fix direction: Reject iteration counts above a ceiling well past mRemoteNG's range with `ImportException`, and keep a
name-to-counter map in `CredentialName`.

### S-34: `Orrery.Core` pins BouncyCastle 2.6.2, which has 21 published CVEs; the app ships 2.7.0 only because SSH.NET requires it
Severity: Low      Verdict: CONFIRMED
Where: src/Orrery.Core/Orrery.Core.csproj:3; no `packages.lock.json`, `Directory.Packages.props` or repo `nuget.config`
Failure: If SSH.NET's floor ever drops, or a project ships without SSH, the app falls back to 2.6.2. 2.6.2 is affected by
the BouncyCastle advisories of 2026-10-02 (CVE-2026-63569, CVE-2026-103600 and others), all fixed in 2.7.0. NuGet's
advisory feed doesn't map them yet, which is why the baseline vulnerability check came back clean.
Evidence: `src/Orrery.App/obj/project.assets.json` resolves 2.7.0; `src/Orrery.Core/obj/project.assets.json` resolves
2.6.2 (checked directly). Orrery's own BouncyCastle use (AES-GCM and PBKDF2 for import) doesn't touch the affected code.
Rule: none
Fix direction: Raise the Core reference to 2.7.0. Consider lock files with locked restore in the packaging scripts.

---

## Hardening notes (no finding)

These came up during the pass and don't cross a boundary on their own:
- Set `EnableCredssp = true` and `EnableTls = false` explicitly in `RdpSession.BuildConfig` and assert
  `ShouldPerformCredssp()` after the TLS upgrade. Today's IronRDP default already requests NLA only. There are no
  RDP tests at all.
- Nothing pins `CertificateTrustDecision.Reject` to enum value 0, yet closing either trust prompt relies on it.
- `SshLogin` and `ConnectPromptResult` are records whose generated `ToString` would print secrets; nothing formats
  them today.
- A damaged `trusted-certificates.json` or `known-hosts.json` throws from the `MainWindow` constructor, unlike
  `connections.json`, which is moved aside.
- `RdpSession.ReactivateAsync` writes without `_writeLock` and can overlap an input write.
- The terminal title (OSC 0/2) is stored raw, up to 4094 characters with control characters. Nothing displays it
  today; strip it before anyone wires `TitleChanged` up.
- SGR 8 from the host also hides Orrery's own "[Disconnected]" line, and an ESC inside OSC replays the next byte as
  text. These are correctness bugs.
- The VNC library loads an ambient `libturbojpeg` if one is installed, so Tight-JPEG uses whatever version the host has.
- `Avalonia.BuildServices` sends build telemetry; `AVALONIA_TELEMETRY_OPTOUT=1` turns it off. It isn't in the shipped app.
