# Orrery design: recording sessions and playing them back

Status: **Draft**, 2026-11-01
Scope: the "Session recording and replay" item in [roadmap.md](roadmap.md). Builds on the data folder (§2.4) and the
credential stores (§3) of [design-storage-vault-import.md](design-storage-vault-import.md).

---

## 1. Goals and non-goals

**Goals**
- Record a session, when asked, so it can be watched again later: for an audit, or for "what did I do on that box".
- Terminal sessions (SSH, Telnet, local terminals, serial) as text, which is small and searchable.
- Remote desktops (RDP, VNC) as a screen recording, from the frame the view already has.
- Turned on per connection (an inheritable setting, so a folder can record everything in it) or per session from the
  tab menu. While a session records, its tab says so.
- A player in Orrery for both kinds, so recordings need no other software.
- A stolen data folder gives away no more than it does today: recordings are encrypted.
- Recording never needs the vault unlocked, so a recorded connection opens as quickly as any other.
- Old recordings are deleted by age and by total size.

**Non-goals**
- Recording what the user types. Terminal recordings hold what the remote side sent back, never keystrokes, so a
  password typed at a prompt that doesn't echo it isn't recorded.
- Sound, the clipboard and file transfers.
- Bundling a video encoder. Screen recordings export to Motion-JPEG `.avi` with nothing extra, and to MP4 only through
  an ffmpeg the user has installed (§3.4).
- Recording on someone else's behalf: there's no server, upload or central store. Recordings stay on this computer,
  in the data folder.
- Telling the remote side or its other users that the session is being recorded. Orrery can't, for any of these
  protocols. The setup text says so (§5.1).

---

## 2. What gets recorded

### 2.1 Terminal sessions

The terminal view already receives every byte the session sends back (`ITerminalSession.DataReceived`). The recorder
listens to the same event and writes what arrives, with the time since the recording started, in
[asciicast v2](https://docs.asciinema.org/manual/asciicast/v2/) form:

- A header with the terminal's size, the start time and the connection's name (not its host, §4.4).
- An `"o"` event for each piece of output, decoded as UTF-8. Bytes that aren't valid UTF-8 are kept as they are
  inside the encrypted file (§3.2), so the player feeds the emulator exactly what the session did.
- An `"r"` event each time the terminal is resized.
- A `"m"` (marker) event when the session disconnects, reconnects or ends, so the player can show where.
- Never an `"i"` (input) event.

Orrery's notices in the terminal (the grey `»` lines, such as Serial's) are output like any other and are recorded too.

### 2.2 Remote desktops

The view gets `FrameUpdated` with a damaged rectangle, and copies pixels out with `CopyRegion`. The recorder does the
same, on its own thread and at its own pace:

- **Frame rate.** At most 5 frames a second by default (1, 2, 5 or 10 in Settings). Damage that arrives between two frames is merged into one rectangle per
  area (a short list, not a single union, so a clock in one corner and a window in another don't make a full-screen
  frame). When nothing changed, nothing is written.
- **Key frames.** A whole frame at the start, after every resize, and every 30 seconds, so the player can jump about
  without decoding from the start.
- **Change frames.** Each damaged rectangle, as the difference from the last recorded pixels there (XOR), which is
  mostly zeros and compresses far better than the pixels themselves.
- **Pixels.** RGB, 3 bytes a pixel: the alpha byte is always opaque and isn't kept.
- **Compression.** Each frame is compressed with Brotli at a fast level (`System.IO.Compression`, already part of
  .NET: no new package). It's lossless: text in the recording is as sharp as it was on screen. This is a screen codec,
  as RDP's and VNC's own encodings are, not a video codec such as H.264, H.265 or AV1: those are lossy, need an
  encoder Orrery would have to ship, and only matter for export (§3.4).
- **The pointer.** RDP and VNC draw the remote pointer locally, so it isn't in the frame. The recorder writes the
  pointer's position with each frame, and the player draws a plain arrow there. The remote pointer's own shape isn't
  recorded.
- **Several monitors.** A session spread over several displays has one frame buffer covering them; it's recorded as
  one picture, as the view has it.

**Size.** An idle desktop costs next to nothing. Typing in a 1920×1080 desktop is a few kilobytes a frame. Video
playing inside the session is the worst case: the change frames are then nearly whole frames, roughly 1 to 3 MB
each at 5 frames a second. A single recording therefore stops at a size limit (§4.3) rather than filling the disk.

### 2.3 When a recording starts and stops

- With the setting on, recording starts when the session first connects, and the same file continues across
  automatic reconnects (with a marker). A new tab for the same connection is a new recording.
- From the tab menu, *Start recording* starts now, and *Stop recording* stops and closes the file. Turning it on from
  the menu doesn't change the connection's setting.
- Closing the tab, or Orrery quitting, closes the file. If Orrery crashes, everything up to the last chunk (§3.2) can
  still be played; the player says the recording ended without being closed.
- View-only sessions are recorded like any other.

---

## 3. Files and encryption

### 3.1 Where they go

`recordings/` in the data folder, one file per recording:

```
recordings/2026-11-01 14.02.33 web-01 3f9a.orrec
```

The name is the start time (local), the connection's name with characters a file name can't hold replaced, and four
random hex digits so two tabs started in the same second don't collide. The connection's name is in the file name so
the folder can be read at a glance; a user who considers names sensitive can turn that off (§4.2), and the file is
then named by time only.

`DataFolderFiles.Folders` gains `("recordings", "*.orrec")`, so Move data copies recordings and deleting the old copy
removes them. Move data says how much it's about to copy when recordings make it large.

### 3.2 Format

Every recording is encrypted, whatever kind it is. One file format holds both:

```
"ORREC" 0x01                      magic and version
header length (u32), header JSON  not secret: format, kind ("terminal" or "screen"), started, ended*, chunk size,
                                  the wrapped file key (below). *Written at close; absent after a crash
chunk…                            XChaCha20-Poly1305 over up to 64 KB of the recording's events
```

- **File key.** Each recording has its own random 256-bit key.
- **Wrapping.** The file key is sealed to the *recordings public key* (§3.3): an X25519 key agreement between a fresh
  key pair made for this file and the recordings key, then HKDF-SHA256 to a wrapping key, then XChaCha20-Poly1305
  over the file key. The fresh public key and the sealed file key go in the header. Orrery can therefore write a
  recording with only the public key, which isn't secret.
- **Chunks.** Events are buffered and sealed as a chunk at 64 KB, or after 2 seconds, whichever comes first, so a
  crash loses at most 2 seconds. Each chunk's nonce is the file's random nonce prefix plus the chunk's number, and its
  associated data is the header plus the chunk's number and whether it's the last. So chunks can't be reordered,
  dropped from the middle or moved between files without the player noticing, and a file cut short reads as "ended
  without being closed" rather than as complete.
- **Inside a chunk.** For a terminal: asciicast v2 lines. For a screen: frames as length, type (key frame, change
  frame, resize, pointer, marker), time and payload.
- **Header contents.** Nothing about the host or user: the connection's name and protocol are inside the encrypted
  part. The header says only what's needed to open it.

### 3.3 The recordings key

One X25519 key pair per data folder, made the first time anything is recorded:

- **Public key:** in `settings.json` (`recordings.publicKey`). Not secret: knowing it only lets someone make a file
  Orrery would open, which the chunk check doesn't stop and doesn't need to (§6).
- **Private key:** in the credential store this data folder uses, as a hidden entry. In the vault it's an entry in the
  vault's contents, never shown in the credential list, carried through master password changes (which re-wrap the
  vault's data key, not its contents) and through Move data with the vault. In the OS keychain it's the item
  `orrery:recordings-key`.
- **Playing** needs the private key, so it needs the store open: with the vault, that's the usual unlock window
  (with Windows Hello, if on).
- **Switching the credential store** (vault file to keychain or back) moves the private key with the credentials.
- **Lost key.** If the private key is gone (the keychain item deleted, a vault replaced by a new one), recordings made
  before can't be played. Orrery says so, makes a new pair the next time it records, and leaves the old files for the
  user to delete. They aren't deleted for being unreadable.

Why a key pair rather than the vault's own key: a recording often starts when the session connects, and a vault
that's locked would have to be unlocked first. With a public key, recording never asks for anything, and a locked
vault stays locked.

### 3.4 Exporting for other software

Exporting is the only way a recording leaves Orrery's encryption. The player's *Export…* first says the copy will be
readable by anyone who has the file, then writes it where the user chooses:

- **Terminal recordings:** asciicast v2 (`.cast`), which asciinema's player and asciinema.org play and its `agg` tool
  turns into a GIF.
- **Screen recordings, built in:** Motion-JPEG in an AVI file (`.avi`), which VLC and Windows Media Player play.
  Frames are encoded as JPEG (quality 85) with SkiaSharp, which Avalonia already ships; the AVI container is written by
  Orrery (RIFF, `hdrl`, `movi` and an `idx1` index; OpenDML for files past 1 GB). Frames are written at the
  recording's frame rate, repeating the last frame through idle stretches, with the pointer drawn in.
- **Screen recordings, through ffmpeg:** when Settings › Recordings names an ffmpeg executable, export also offers MP4
  with H.264 (default), H.265 or AV1, each listed only if that ffmpeg has an encoder for it (`ffmpeg -encoders`, read
  once). Orrery starts ffmpeg by its full path with arguments as a list (no shell), pipes raw RGB frames to its standard
  input, and writes nothing to disk unencrypted except ffmpeg's output file. ffmpeg is never searched for on `PATH`,
  downloaded or bundled: the user picks the file, and Settings shows its version line.

Export runs in the background with progress and *Cancel*; a canceled export deletes its partial file.

---

## 4. Settings

### 4.1 Per connection

`InheritableSettings.RecordSessions` (`bool?`, inherited like the others; off by default). In the connection editor
under a new *Recording* group, for every protocol.

### 4.2 Settings › Recordings

- *Keep recordings for*: 7, 30 (default), 90 or 365 days, or until deleted.
- *Use at most*: 1, 5 (default), 20 or 50 GB for all recordings together.
- *Stop a recording at*: 1 (default), 4 or 16 GB. A recording that reaches it stops, says so on the tab, and the
  session carries on.
- *Frames per second for remote desktops*: 1, 2, 5 (default) or 10.
- *Put connection names in file names* (on by default).
- *ffmpeg for MP4 export*: none by default; *Choose…* picks the executable (§3.4).
- *Open the recordings folder*, and the space recordings use now.

### 4.3 Retention

At startup and each time a recording closes, Orrery deletes recordings older than the limit, then the oldest until the
total is under the limit. It never deletes a recording that's being written or played. Deleting them is permanent
(they're Orrery's own files, as backups are); the setting says so. A file in `recordings/` that isn't an `.orrec` file
is left alone.

### 4.4 What isn't recorded where

The host, user name and port are never in a file name or header, only inside the encrypted part, and only the
connection's name and protocol are kept there: enough to say what the recording is. The log records that a recording
started and stopped, with the file name, and nothing from inside it.

---

## 5. Interface

### 5.1 Turning it on

The first time recording is turned on (in a connection, or from a tab), a one-time note says:
- Recordings hold everything the session showed: what's on screen, and every line a terminal printed.
- They're encrypted, and play only in Orrery on a computer with this data folder's credentials.
- The remote side isn't told. Recording other people's sessions may need their agreement.

### 5.2 While recording

- The tab header shows a red dot before the name, with "Recording" for screen readers and in its tooltip.
- The tab menu shows *Stop recording* (or *Start recording*).
- A recording that stopped by itself (size limit, disk full, the key couldn't be made) says why once, in the tab's
  notice bar, and the dot goes.

### 5.3 The player

*View › Recordings* opens a window listing recordings (newest first) with the connection's name, protocol, start
time, length and size, read from the header and the first chunk after the store is opened. From the list: *Play*,
*Export…*, *Show in folder*, *Delete*.

Playing:
- **Terminal:** a read-only terminal view fed by a `TerminalEmulator`, play and pause, speed (½×, 1×, 2×, 4×, 8×), a
  timeline with the markers, and *Skip idle* (gaps over 2 seconds play as 2 seconds; on by default). Seeking replays
  the output from the start into a fresh emulator without drawing, which is fast for text. *Find* searches the
  recording's text and jumps to each match. *Export…* (§3.4).
- **Screen:** the frame, scaled to the window, with the same controls. Seeking decodes from the key frame before the
  point.
- A recording that ended without being closed plays to where it stops, with "This recording ended without being
  closed, probably because Orrery stopped" at the end.
- A recording that fails its check partway plays to the last good chunk and says the rest couldn't be read.

---

## 6. What an attacker can and can't do

| Attacker | Can | Can't |
|---|---|---|
| Has a copy of the data folder | See how many recordings there are, when they were made, how big they are, and the connection names in file names (unless turned off) | Read any of them: the private key isn't in the folder in keychain mode, and in vault mode it's inside the vault, behind the master password |
| Has the data folder and the master password | Play them, as they could read the saved passwords | — |
| Another local user | Nothing new: the data folder is private to the user (`PrivateStorage`) | — |
| Plants a crafted `.orrec` file in the folder | Make the player open a file Orrery didn't write (the public key isn't secret) | Get anything past the player's checks: the header, chunk sizes, frame sizes and rectangles are all checked against limits before anything is allocated (§8) |
| A remote host | Send output designed to look odd when played back: the same as it can do live, through the same emulator | Reach anything through the recorder: it writes what it received and parses nothing |
| Malware running as the user | What it can do today | (Accepted risk: same-user code execution) |

---

## 7. Later

- MP4 export without ffmpeg, through the platform's own encoder (Media Foundation on Windows, AVFoundation on macOS).
- Recording sound.
- Recording the remote pointer's shape.
- A *Recordings* entry in the tab's context menu that opens the player at this connection's recordings.

---

## 8. How it's built

- `Orrery.Core/Recording/RecordingFile.cs`: the format, chunk sealing and opening, the header, the limits.
- `Orrery.Core/Recording/RecordingKeys.cs`: the key pair, wrapping and unwrapping the file key, keeping the private key
  in the vault or keychain.
- `Orrery.Core/Recording/TerminalRecorder.cs` and `ScreenRecorder.cs`: listening to a session and writing chunks, on a
  background thread with a bounded queue (a recorder that falls behind drops screen frames, never terminal output,
  and never slows the session).
- `Orrery.Core/Recording/ScreenFrames.cs`: damage merging, XOR, Brotli, and decoding.
- `Orrery.Core/Recording/RecordingRetention.cs`: the age and size limits.
- `Orrery.App/Recording/RecordingsWindow.cs` and `PlayerView.cs`: the list and the player.
- `Orrery.Core/Recording/AviWriter.cs` (the container) and `Orrery.App/Recording/RecordingExport.cs` (`.cast`, JPEG
  frames through SkiaSharp, and ffmpeg).
- Changes to `TerminalView`, `RemoteFrameView` (start and stop the recorder), `SessionTabHeader` (the dot),
  `InheritableSettings`, the connection editor, Settings and `DataFolderFiles`.

**Limits a file must keep** (checked before allocating): header at most 64 KB; chunks at most 64 KB plus the tag;
frames at most 8192×8192; rectangles inside the frame; a decompressed frame no larger than its rectangle says;
at most 1,000 rectangles a frame.

### 8.1 Tests

- `RecordingFileTests`: sealing and opening, a wrong key, chunks reordered, dropped, cut short or moved from another
  file, a header edited, every limit, a crafted file at each limit and one byte past it.
- `RecordingKeysTests`: making the pair once, keeping the private key in a vault and in a fake keychain, a master
  password change, switching stores, a lost key.
- `TerminalRecorderTests`: output and resizes recorded with times, no input ever, invalid UTF-8 kept, reconnect
  markers, crash recovery (an unclosed file plays).
- `ScreenRecorderTests`: damage merging, the frame rate cap, key frames, XOR round trips, the pointer, resizes, the
  size limit, frames dropped when the queue is full.
- `RecordingRetentionTests`: age and size limits, files being written or played, files that aren't recordings.
- `SecretLeakTests`: the file key, the private key and recorded output stay out of the log.
- Headless UI tests: the tab's dot and menu, the one-time note, the player's controls, seeking, *Find*, *Export…*, and the messages for a cut-short or damaged file.
- `RecordingExportTests`: a `.cast` export plays back the same output; an `.avi` export has a valid RIFF structure,
  frame count and index; ffmpeg arguments are built as a list and never through a shell; a canceled export leaves no
  file. ffmpeg itself isn't run in tests.

### 8.2 Phases

1. The file format, keys, terminal recording, retention and the terminal player.
2. Screen recording and the screen player.
3. Settings polish, the README and the roadmap.
