# Orrery design: recording sessions and playing them back

Status: **Built**, 2026-11-01. Not yet tried with real sessions in the app; see §8.3
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
- The clipboard and file transfers. Sound is recorded only for RDP, which plays it through IronRDP (§2.2).
- Bundling a video encoder. Screen recordings export to Motion-JPEG `.avi` with nothing extra; to MP4 through Windows'
  own encoders on Windows; and through an ffmpeg the user has installed anywhere (§3.4). Bundling ffmpeg would mean
  meeting its license for each build (GPL with x264 and x265), the H.264 and H.265 patent pools, and 80–130 MB a
  platform; the operating system's encoders come with their licenses paid.
- Recording on someone else's behalf: there's no server, upload or central store. Recordings stay on this computer,
  in the data folder.
- Telling the remote side or its other users that the session is being recorded. Orrery can't, for any of these
  protocols. The setup text says so (§5.1).

---

## 2. What gets recorded

### 2.1 Terminal sessions

The terminal view already receives every byte the session sends back (`ITerminalSession.DataReceived`). The recorder
listens to the same event and writes what arrives, with the time since the recording started, as events of its own
(§3.2) that map one to one onto [asciicast v2](https://docs.asciinema.org/manual/asciicast/v2/), which export writes:

- An info event with the connection's name, protocol and start time (inside the encryption, §4.4).
- An output event (asciicast `"o"`) for each piece of output, as the bytes arrived. Bytes that aren't valid UTF-8 are
  kept as they are, so the player feeds the emulator exactly what the session did; export decodes them.
- A resize event (`"r"`) each time the terminal's grid changes.
- A marker event (`"m"`) when the session drops or disconnects, and when a reconnect carries on in the same file.
- Never an input event (`"i"`).

Orrery's notices in the terminal (the grey `»` lines, such as Serial's) are output like any other and are recorded too.

### 2.2 Remote desktops

The view gets `FrameUpdated` with a damaged rectangle, and copies pixels out with `CopyRegion`. The recorder does the
same, on its own thread and at its own pace:

- **Frame rate.** At most 5 frames a second by default (1, 2, 5, 10, 15, 30 or 60 in Settings). The rate is a ceiling, not a promise: a frame is taken only when the screen changed, so a desktop that sends 15 a second records 15 even at 60. Windows sends about 30 unless its server is set for 60. Orrery doesn't measure the rate a desktop sends, and RDP and VNC don't report one. Damage that arrives between two frames is merged into one rectangle per
  area (a short list, not a single union, so a clock in one corner and a window in another don't make a full-screen
  frame). When nothing changed, nothing is written.
- **Key frames.** A whole frame at the start, after every resize, every 30 seconds, and once the changes since the
  last one are 4 times its size, so the player can jump about without decoding from the start. The last rule keeps a
  seek short however high the frame rate, for at most about a quarter more file.
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
- **Sound (RDP).** IronRDP decodes the server's sound and plays it itself, so Orrery never sees it; the fork (branch
  `orrery/audio-tap`) gives each connection an *audio tap* shared by the static channel and the two dynamic ones.
  While a recording is attached the tap keeps a copy of each decoded block as 16-bit PCM, at most 10 seconds of it,
  dropping the oldest if it isn't taken. The recorder takes it every 40 ms and writes an audio event (sample rate,
  channels, PCM) stamped with when it was taken; all-zero blocks are left out. Stored as played, CD-quality stereo is
  about 11 MB a minute while sound plays. *Record remote desktop sound* in Settings turns it off (on by default). VNC
  has no sound.

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
header length (u32), header JSON  not secret: format, version, kind ("terminal" or "screen"), the recordings key's
                                  id, the sealed file key and the nonce prefix (below)
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
- **Inside a chunk.** Events, run together across chunks as one stream: a type byte, the time in milliseconds
  (32 bits), the payload's length (32 bits) and the payload. Info, output, resize, marker and end for a terminal; info,
  resize, key frame, change frame, pointer, audio, marker and end for a screen. The end event is the last one in a closed file.
- **Header contents.** Nothing about the host or user: the connection's name and protocol are inside the encrypted
  part. The header says only what's needed to open it.

### 3.3 The recordings key

One X25519 key pair per data folder, made the first time anything is recorded:

- **Public key:** in `settings.json` (`recordings.publicKey`). Not secret: knowing it only lets someone make a file
  Orrery would open, which the chunk check doesn't stop and doesn't need to (§6).
- **Private key:** in the credential store this data folder uses, as a credential with a reserved id
  (`RecordingKeys.CredentialId`) that stores leave out of their list, so it's never shown, picked or exported. In the
  vault it's carried through master password changes (which re-wrap the vault's data key, not its contents) and
  through Move data with the vault. In the OS keychain it's an item like any saved credential's.
- **Playing** needs the private key, so it needs the store open: with the vault, that's the usual unlock window
  (with Windows Hello, if on).
- **Switching the credential store** (vault file to keychain or back) moves the private key with the credentials.
- **Settings lost.** If `settings.json` loses the public key, the next recording works it out again from the private
  key, which needs the store open once. A new pair is only made when there's no private key at all.
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
  Orrery (RIFF, `hdrl`, `movi` and an `idx1` index). Plain AVI is limited in size, so a long export is split into
  parts of 1 GB (`name (part 2).avi`) rather than written as OpenDML. Frames are written at the recording's frame
  rate, with an empty chunk for a frame that repeats the last (players hold the picture), and the pointer drawn in.
  A recording with sound gets a second stream of 16-bit PCM (`auds`, `01wb` chunks after each frame).
- **Screen recordings, through Windows:** MP4 with H.264 (default), H.265 or AV1, each listed only if Windows has an
  encoder for it (`MFTEnumEx`): H.264 everywhere but the N editions, H.265 with a graphics card or the HEVC extension,
  AV1 with a recent graphics card, marked "(graphics card)". Media Foundation's sink writer takes NV12 pictures that
  Orrery converts from RGB (BT.709, 16–235), at the recording's frame rate and about a quarter of a bit a pixel. The
  size is rounded up to even sides, or to 16-pixel blocks for a graphics card's encoder, padded with black. The
  graphics card is used only for a codec Windows has no software encoder for: with it on for H.264, the sink writer
  hung in `Finalize` on the PC this was built on. The calls are raw COM by vtable slot, from the Windows SDK headers,
  as the Windows Hello code does. Sound goes in as AAC at 192 kbit/s (the sink writer's AAC encoder, given PCM).
- **Screen recordings, through ffmpeg:** when Settings › Recordings names an ffmpeg executable, export also offers MP4
  with any of H.264, H.265 or AV1 that Windows can't make, each listed only if that ffmpeg has an encoder for it
  (`ffmpeg -encoders`, read once). This is how macOS and Linux make MP4. Orrery starts ffmpeg by its full path with arguments as a list (no shell), pipes raw RGB frames to its standard
  input, and writes nothing to disk unencrypted except ffmpeg's output file. ffmpeg is never searched for on `PATH`,
  downloaded or bundled: the user picks the file, and Settings shows its version line. Sound goes to it through a
  second pipe, so it never touches the disk either: a named pipe only this user can open on Windows, or a FIFO in a
  private temporary folder elsewhere. ffmpeg encodes it as AAC at 192 kbit/s. ffmpeg reads its inputs in step, so a
  sound track that stopped while the picture went on would leave both waiting: the sound runs on in silence to two
  frames past each picture.

**Sound in exports.** The sound track is 44.1 or 48 kHz as recorded (other rates are resampled to 48 kHz), mono or
stereo (more channels mixed down to two). Blocks are laid end to end on the export's clock; one stamped up to 120 ms
past where the track has got to follows on (the recorder's timing), and a longer gap is silence. Skip idle shortens
only stretches with neither pictures nor sound.

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
- *Frames a second for remote desktops, at most*: 1, 2, 5 (default), 10, 15, 30 or 60.
- *Record every session* (off by default): the *Record sessions* setting of Defaults for all connections, shown here
  too, so every connection records unless it or its folder turns it off. Quick connections follow it as well; a local
  shell and a file browser don't.
- *Record remote desktop sound* (on by default), with its size: about 11 MB a minute while sound plays.
- *Put connection names in file names* (on by default).
- *ffmpeg for MP4 export* (*ffmpeg (optional)* on Windows): none by default; *Choose…* picks the executable (§3.4).
- *Show recordings* (opens the Recordings section, §5.3), *Open the recordings folder*, and the space recordings use now.
- Which MP4 codec isn't a setting: *Export…* on a screen recording lists AVI and each of H.264, H.265 and AV1 that
  Windows or the chosen ffmpeg can make.

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
- The session toolbar shows a red *REC 1:23* pill. Its panel says how long the recording has run and how big it is,
  and changes it for this recording only: frames a second at most (1, 2, 5, 10, 15, 30 or 60; a screen recording's timer takes the new
  rate at its next frame) and, for a remote desktop that plays sound here, *Record sound*, turned on or off with a
  "Sound on" or "Sound off" marker on the timeline. *Stop recording* and *Show recordings* are under them. While a
  connected session isn't recording, a round button in the same place starts one.
- A recording that stopped by itself (size limit, disk full, the key couldn't be made) says why once, in the tab's
  notice bar, and the dot goes.

### 5.3 The player

The rail has a *Recordings* button, after *Recent*, whose side panel lists recordings newest first: the connection's
name, its protocol badge, when it started ("Today 14:05"), how long it ran (from its start to the file's last write) and
its size, with a red dot and "recording now" for one being made. A filter narrows it by name or protocol, and the
foot gives the count and space used; the folder button opens the recordings folder. Clicking one or pressing Enter
plays it in the main area; the context menu has *Play*, *Export…*, *Show in folder* and *Delete…* (also the Delete
key). Deleting the one playing closes the player first. *Show recordings* in Settings opens the section.

While *Recordings* is the open rail item, the main area (over the sessions and the right-hand pane) is the player, or
"Choose a recording to play" until one is chosen; the side panel stays beside it, whatever its mode. Choosing a session tab,
another rail item, or *Recordings* again goes back to the sessions and pauses the player; coming back carries on.

Names live inside the encrypted part, so they show only while the credential store is open: when it's locked the
rows are the date, kind and size, with *Unlock* above them. The panel reads the key when it refreshes and forgets it
straight after; what each file said is kept only while the store stays open.

Playing:
- **Header:** one line, like a session's toolbar: the protocol badge, the connection's name, then "Recorded <date
  and time> · <length> · <size>" in gray. A long name ends in "…" before the right-hand side, and the subtitle gives
  way first. On the right, *Find* for a terminal recording (the box holds the count and the arrows), an *Export* icon
  (§3.4), and ⋯ with *Show in folder*, *Keyboard shortcuts* and *Delete recording…*. A cut-short or damaged recording
  gets a warning under the header.
- **Picture:** edge to edge, as a live session's is.
- **Transport:** a bar along the bottom with a line above it. The timeline runs across it, with an amber tick for
  each marker (such as a reconnect) and a short blue one under the track for each *Find* match; each shows what it
  is on hover and jumps there on a click. Under it: the time ("1:30 / 4:20") on the left; 10 seconds back, play and
  pause, and 10 seconds forward in the middle; and on the right *Skip idle* (gaps over 2 seconds play as 2 seconds;
  on by default), *Loop* (plays again from the start at the end; off at first, then kept while Orrery runs), speed
  (½×, 1×, 2×, 4×, 8×) and a keyboard button. *Skip idle* and *Loop* are segment buttons, filled when on. Without
  Loop, the end says so and Play becomes *Play again*.
- **Keys:** Space plays and pauses, Left and Right skip 10 seconds, Shift+Left and Shift+Right go to the previous or
  next marker, Home and End go to the ends, [ and ] change the speed, L turns Loop on or off, Delete deletes the
  recording (after asking), and ? shows them all over the picture. Every button's tooltip names its key.
- **Colors:** recordings keep the colors as they were recorded; the player shows them as the viewer has them set. A
  screen is drawn through the color correction from Settings and a terminal with the color-vision palette and
  terminal colors, and both follow a change in Settings while playing. A recording keeps the connection's own
  "Color correction" (`RecordingInfo.ColorCorrection`): one recorded with it off plays in its own colors.
- **Web pages** are the exception to keeping the colors as recorded. The page is corrected in the engine, with an SVG
  `feColorMatrix` filter on its root element in linear light, so the screencast is of the page already corrected.
  The recording says with what (`CorrectedFor`, `CorrectedStrength`; frames shown with another correction, after a
  change in Settings partway, are converted to it as they're recorded). The player undoes that and applies the
  viewer's own correction, nothing when they're the same, and an export undoes it, so the video has the page's own
  colors like every other export. Undoing is exact within rounding except where the correction pushed a color past
  what the screen shows.
- **Terminal:** a read-only terminal view fed by a `TerminalEmulator`. Seeking replays the output from the start into
  a fresh emulator without drawing, which is fast for text. *Find* (Ctrl+F) searches the recording's text as you
  type, says how many matches there are, and Enter, Shift+Enter and the arrows beside it go to the next or previous
  one, playing the recording up to it.
- **Screen:** the frame, scaled to fit, with the same controls. Seeking decodes from the key frame before the
  point. Recorded sound plays on the default output device from ½× to 4×, at the same pitch (`SoundStretch`, which
  overlaps short pieces of the sound, each lined up with the last), not at 8×, while seeking or paused, through the
  IronRDP fork's `AudioOutput`. A recording with sound has a speaker (mute, also M) and a volume slider
  beside *Skip idle*; the volume scales the samples on a square curve, and is kept for the next recording played
  while Orrery runs.
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

- MP4 export without ffmpeg on macOS, through AVFoundation's `AVAssetWriter`. It needs a Mac to build and test on.
- Recording the remote pointer's shape.

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
- `Orrery.App/Recording/RecordingsPanel.cs`, `RecordingStage.cs` and `PlayerView.cs`: the side panel's list, the main area and the player.
- `Orrery.Core/Recording/MediaFoundation.cs` and `Nv12.cs`: MP4 through Windows' encoders.
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
- `Nv12Tests` and `MediaFoundationTests` (Windows): RGB to NV12 colors; an MP4 through each codec this Windows has,
  checked for its boxes, sample entry, frame count and padded size; a canceled export leaves no file.

### 8.2 Phases

All built together, 2026-11-01.

### 8.3 What needs a person

The tests use fake sessions. Still to check in the running app: recording a real SSH, Telnet, local and serial session
and an RDP and VNC desktop; a reconnect carrying on in the same file; the tab's dot; the first-time note; the player
on a long recording (seeking, speed, Find); the vault unlock the first recording asks for; an AVI export in VLC and
Windows Media Player; MP4 exports through Windows (H.264, H.265, AV1) and an installed ffmpeg; and Move data with recordings in the folder.
