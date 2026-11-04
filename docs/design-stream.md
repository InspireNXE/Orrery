# Orrery design: Orrery Stream Protocol and Orrery Host

Status: **Draft**, 2026-11-03 (owner's answers in §12). Scaffolded 2026-11-04: the projects in §3 except
`Orrery.Host.Service` exist, with the media packet header, codec choice and connection options; nothing streams yet.
Scope: a low-latency desktop and game streaming protocol in the spirit of Parsec, direct connect only, with the
client inside Orrery and the host as a separate companion app. MIT throughout.

---

## 1. Goals and non-goals

**Goals**
- A remote desktop that feels local: 60 to 144 fps, GPU-encoded H.264, HEVC and AV1, and a target of under 20 ms from
  the host's screen to the client's on a wired LAN.
- Games played remotely: controller passthrough with rumble, relative mouse with pointer lock, full keyboard capture.
- Useful outside gaming too: CAD, video editing, any workstation where RDP's frame rate isn't enough. The name and UI
  say "stream", not "gaming", for that reason (§2).
- Direct connect only. The client dials the host's address and port; there's no account, relay, signaling server or
  web service.
- Full control over scope: our own host, our own protocol, no dependency on Sunshine or Moonlight.
- MIT for everything we ship. No GPL code, no clean-room work from GPL sources.

**Non-goals**
- Getting through NAT, relays or discovery over the internet. People who want to reach a host over the internet
  bring their own WireGuard or Tailscale.
- Going through jump hosts. UDP can't be carried through an SSH tunnel; the protocol says so in the properties
  pane (§9).
- A host on macOS. There's no virtual gamepad API, and the screen recording and accessibility permissions get in the way
  of running unattended. macOS is client-only.
- 12-bit color. No hardware encoder does it in real time (§5, display capabilities); 10-bit HDR is the ceiling.
- X11. On Linux, host and client are for Wayland sessions only.
- A browser client.
- A launcher or game library. A couch mode driven by a controller is a possible later feature inside Orrery (§11).

---

## 2. Shape and naming

| Part | Where | What it is |
|---|---|---|
| **OSP** (Orrery Stream Protocol) | A protocol in Orrery's tree, next to RDP and VNC | The client: decode, present, input |
| **Orrery Host** | Its own package, same repo, same version | Capture, encode, input injection, pairing; later a Windows service and drivers |

**Why the client is in Orrery.** It's another way to reach a machine, so it takes everything Orrery already gives a
connection: the tree, folder inheritance, credentials in the secret store, Wake-on-LAN, host status, quick connect,
accessibility, theming, updates and popout. A separate client would have to rebuild all of that.

**Why the host is separate.** It runs a SYSTEM service, listens on the network and later installs drivers. Someone
who only wants a connection manager shouldn't get any of that, even switched off. It needs admin rights to install,
while the client stays portable. Its user is whoever sets up the machine being reached, who may never open Orrery.
The Windows installer may later offer the host as an optional component; it stays a separate install.

**Why one repo and one version.** Client and host share the protocol code, so they can't drift apart, and
"host 0.3 works with client 0.3" is easy to explain. One CI run builds both packages. Splitting into a second repo
pays off only if the host gets its own contributors or release schedule.

In the protocol dropdown it shows as **OSP**, with the tooltip "Orrery Stream Protocol: low-latency desktop and game
streaming (requires Orrery Host)". Quick connect accepts `osp://host:port`.

---

## 3. Projects

```
src/
  Orrery.Streaming/           shared: protocol messages, QUIC wrapper, FEC, pairing, codec and capability negotiation
  Orrery.Protocols.Stream/    client: session, decoders, presenter, input capture, SDL3 controllers
  Orrery.Host/                host: tray app, capture, encoders, input injection, audio, pairing UI
  Orrery.Host.Service/        Windows SYSTEM service and secure-desktop helper (phase 4)
drivers/                      virtual gamepad, IddCx display, virtual audio (phase 6)
```

`Orrery.Streaming` has no UI and no platform code, so both sides and the tests can use it.

---

## 4. Transport: QUIC over MsQuic

One QUIC connection per session, on one UDP port (default to be picked; configurable on both sides).

| Traffic | Carried on | Why |
|---|---|---|
| Video, audio | Unreliable datagrams (RFC 9221) with our own FEC | No head-of-line blocking; a late frame is a useless frame |
| Control: codec and mode changes, keyframe and reference-invalidation requests, stats | A reliable bidirectional stream | Must arrive, low volume |
| Input: keys, buttons, controller state | A reliable stream to start with; datagrams with redundancy if measurements show the stream adds latency | Lost key-ups cause stuck keys |
| Clipboard: offers on one stream, each copied file on its own (§10) | Reliable streams | Large items mustn't hold up input |

**What QUIC gives us**: TLS 1.3 built in (pairing becomes pinning the host's certificate, §6), one firewall port,
multiplexing, and connection migration so a laptop moving from Wi-Fi to Ethernet keeps its session.

**Library.** `System.Net.Quic` doesn't expose datagrams and doesn't support macOS, so we call **MsQuic** (MIT)
directly through P/Invoke and ship its native library per runtime, as IronRdp's is shipped. Its source, version and
hash get checked before it's added, and its macOS build needs particular care.

**Congestion control.** QUIC applies congestion control to datagrams too, and its standard algorithms aim for throughput,
not timeliness. We pick BBR in MsQuic and add our own rate control above it: the client reports one-way delay
trends, loss and decode-queue depth, and the host adjusts encoder bitrate (and, under sustained trouble, resolution
or frame rate), in the spirit of WebRTC's delay-based estimator. The QUIC layer must never queue video for long; if
the send buffer backs up, the host drops frames rather than delivering them late.

**Packetization.** Datagram payloads are about 1,200 bytes. Each encoded frame is split into numbered packets with a
small header (frame number, packet index and count, FEC group, timestamps), then protected by Reed-Solomon FEC
whose overhead follows recent loss (default around 10 to 20%). The FEC implementation is ours or an MIT/BSD
library, checked before adding.

**Recovering from loss.** When FEC can't rebuild a frame, the client asks for a recovery frame instead of a full keyframe:
reference-frame invalidation where the encoder supports it (NVENC does), otherwise intra-refresh, with a full
keyframe as the last resort.

---

## 5. Video

### Host: capture

| Platform | API | Notes |
|---|---|---|
| Windows | Windows.Graphics.Capture, with DXGI Desktop Duplication as fallback | Stays on the GPU as a D3D11 texture; cursor sent separately so it can be drawn on the client with no delay |
| Linux | xdg-desktop-portal ScreenCast plus PipeWire, DMA-BUF | The portal asks the user once; a restore token avoids asking again. Unattended capture before login isn't possible on Wayland |

### Host: encode

Called directly, not through FFmpeg, for full control over low-latency settings and no LGPL code to ship:

| Vendor | API | License |
|---|---|---|
| NVIDIA | NVENC (Video Codec SDK headers) | MIT headers |
| AMD | AMF | MIT |
| Intel | oneVPL | MIT |
| Linux, any | VA-API | MIT |

Settings for every encoder: low-latency preset, no B-frames, one frame in flight, CBR or capped VBR driven by the
rate control above, infinite GOP with recovery as in §4, intra-refresh available. **NVENC first** (best documented for
low latency), **AMF immediately after**, so the encoder interface is shaped by both from the start. Both GPUs are
available for testing. Intel and VA-API come with the Linux host.

AV1 encoding needs an RTX 40 or RX 7000 series card or newer; the host advertises what its GPU can actually do.

### Client: decode and present

| Platform | Decode | Present |
|---|---|---|
| Windows | D3D11VA (DXVA), or Media Foundation | D3D11 flip-model swapchain, tearing allowed, VRR |
| Linux (Wayland) | VA-API to DMA-BUF | Vulkan, in a native Wayland window from SDL3 (below) |
| macOS | VideoToolbox | Metal layer |

FFmpeg's libavcodec, dynamically linked under LGPL, is the fallback if a native decoder path gets painful; that's still
compatible with MIT.

AV1 decoding needs an RTX 30, RX 6000, Intel Arc or 11th-gen, or Apple M3 or newer. The client picks the best codec both
sides support (AV1, then HEVC, then H.264) unless the connection names one.

**Presenting frames is the riskiest part.** The decoded frame must stay on the GPU all the way to the screen. Two ways in Avalonia:

1. **A native child surface** (`NativeControlHost`) with its own swapchain. Lowest latency, tearing and VRR work, but
   Orrery's controls can't draw on top of it (airspace).
2. **Avalonia's GPU texture interop.** Composes cleanly, but adds a compositor frame.

3. **A window of its own from SDL3**, outside Avalonia, used by the popout (§8). No airspace problem, and one
   presentation path on every platform.

The popout window (§8) is the main home for a stream. **On Linux it must be option 3.** Orrery's window is an X11 one
under XWayland ([packaging](packaging.md)), and under XWayland a stream gets no HDR, unreliable VRR, emulated pointer
lock and keyboard capture, and no say over which display it goes full screen on. SDL3, which we add for controllers
anyway, opens a native Wayland window beside Orrery's and gives the stream:

- full screen on a chosen display (`xdg_toplevel.set_fullscreen` with an output), so one stream per monitor works;
- relative-pointer, pointer-constraints and keyboard-shortcuts-inhibit for real pointer lock and key capture;
- tearing-control for VRR, where the compositor supports it;
- the color-management protocol for HDR, where the compositor supports it (recent KDE; GNOME catching up);
- Vulkan presentation with VA-API frames imported as DMA-BUF, no CPU copy.

Orrery's own window stays on XWayland; the tree, properties and overlay hotkeys keep working beside the stream. If
Avalonia's Wayland backend matures, the stream path doesn't change.

On Windows and macOS, the phase 1 spike tries option 3 against option 1 and keeps whichever measures faster; SDL3
everywhere is preferred if it isn't slower, since it means one presentation path. Tabs use option 1 or 2.

Today's `IFrameBufferSession` (RGBA copied on the CPU, damage rectangles) is the wrong shape for this. OSP gets a new
session interface, roughly `IVideoStreamSession`, that hands the view a GPU surface to present into instead of pixels to copy.

### Display capabilities

What can run together, at low latency, on a wired LAN:

| Capability | Supported | How | Limits |
|---|---|---|---|
| Several displays | Yes | One capture, encode and stream per monitor; each shown in its own popout, full screen on a chosen client display | Encoder sessions and throughput on the host: two 4K120 streams can saturate a single-engine GPU. Streams aren't frame-synchronized with each other |
| HDR | Yes on Windows; best effort on Linux | FP16 capture, a shader to 10-bit PQ BT.2020, HEVC Main10 or AV1 10-bit with HDR metadata; client decodes P010 and presents through an HDR10 swapchain (Windows), the Wayland color-management protocol (Linux) or EDR (macOS) | HEVC or AV1 only; H.264 doesn't do HDR. PipeWire HDR capture and Wayland HDR are still new upstream |
| VRR | Yes, end to end | Host encodes each frame as the game produces it, no fixed stream rate; client presents each frame as soon as it's decoded, tearing allowed, and the monitor's VRR keeps it smooth | A headless host needs a high-refresh display to avoid capping the game; the virtual display in phase 6 provides one |
| 10-bit color | Yes | HEVC Main10, AV1 10-bit | Needs HEVC or AV1 on both sides |
| 12-bit color | **No** | | NVENC, AMF and Intel encoders stop at 10-bit; real-time software encoding at 4K would break the latency budget. Few consumer panels are true 12-bit |
| 4:4:4 chroma | Optional, where both sides can | HEVC 4:4:4 (NVENC encodes it), for sharp text in desktop use | Decode is fine on NVIDIA and Intel, patchy on AMD; AV1 4:4:4 has no hardware support. Falls back to 4:2:0 |

**Latency budget** for one 4K HDR stream at 120 fps on wired gigabit:

| Step | Time |
|---|---|
| Capture | 1–2 ms |
| Color conversion | under 1 ms |
| Encode | 3–6 ms |
| Network | under 1 ms |
| Decode | 2–4 ms |
| Present, with VRR | 0 to a few ms |
| **Total** | **about 8–15 ms**, plus the display's own lag |

Two such streams at good quality need roughly 150–300 Mbps: fine on wired gigabit, unrealistic on most Wi-Fi, where
rate control lowers quality before it lets latency grow. The host advertises what its encoders can sustain, and the
client offers only combinations within it.

---

## 6. Pairing and security

- **Pairing.** The host shows a short PIN; the client enters it in a "Pair…" dialog. A PAKE (for example SPAKE2 or
  CPace, from an MIT/BSD/Apache library or NSec/BouncyCastle, which Orrery already uses) over the first QUIC stream
  proves both sides saw the same PIN, and each side then pins the other's certificate.
- **Storage.** The client keeps the host's certificate fingerprint and its own client key in Orrery's secret store, like
  passwords. The host keeps a list of paired clients, each one revocable in its UI.
- **Every connection** is mutual TLS 1.3 with pinned certificates; an unpaired client gets nothing past the handshake.
- **Host policy** per paired client: view only, keyboard and mouse, controllers, clipboard (off, text only, one
  direction, or everything; §10), which monitors.
- **Exposure.** The host listens only on the interfaces and port its settings allow, LAN only by default.
  A security review of the host goes before any release that includes it.

---

## 7. Input

**Client capture**
- Keyboard: scancodes, as RDP sends today (`ScancodeMap`, `WindowsKeyHook`). On Wayland, keyboard-shortcuts-inhibit
  for full capture, within its known limits.
- Mouse: absolute for desktop use; **relative with pointer lock** for games. Raw Input on Windows; relative-pointer and
  pointer-constraints on Wayland; macOS through CGAssociateMouseAndMouseCursorPosition. The host tells the client
  when a game hides the cursor, so it can switch modes automatically, with a key to override.
- Controllers: **SDL3** (zlib). XInput, DualSense (gyro, touchpad, lightbar), DualShock 4, Switch Pro. Up to four
  controllers; rumble and lightbar are sent back from the host.

**Host injection**
- Windows: SendInput for keyboard and mouse. Gamepads first through a virtual HID device from user mode (no driver,
  but games that only read XInput won't see it), then a signed XInput-compatible driver in phase 6. ViGEmBus is retired
  and not an option.
- Linux (Wayland): uinput for everything to start with; gamepads need it regardless. The host package grants access to
  `/dev/uinput` with a udev rule. The RemoteDesktop portal with libei (GNOME 46+, KDE 6.1+) comes later as an option
  for keyboard and mouse that asks the user's permission through the compositor.

---

## 8. Client UI in Orrery

- **Popout as the main home.** A popped-out stream owns its window: its own swapchain, borderless full screen,
  pointer lock and keyboard capture scoped to that window.

**What OSP needs from popout** (being built in another session). None of this has to work for RDP or VNC on day one,
but the design shouldn't rule it out:

1. A session type can supply **its own window** for the popout instead of an Avalonia one: an SDL3 window on Linux,
   and possibly on every platform (§5). Popout manages it (open, close, return to a tab, title, closing the session)
   without assuming it's an Avalonia `Window`.
2. If it is an Avalonia window, the session can fill it with a **native surface** and nothing else draws over it.
3. **Full screen on a chosen display**, including on Linux when the window is native Wayland.
4. **Several popouts from one session**, one per streamed monitor, closed together.
5. **Input capture scoped to the popout**: pointer lock and keyboard capture apply only while it has focus, release
   on focus loss (as `ReleaseAllKeys` does), and session escape still works.
6. Popout as a **per-connection default** ("open in popout"), so a stream can skip the tab entirely.
- **Tabs also work**, with somewhat higher latency, for casual or desktop use.
- **Overlay** on a hotkey: stats (fps, bitrate, latency, loss, codec, decoder), mouse mode, disconnect. In the popout
  it's drawn into the swapchain or a layered window, never over a native surface in a tab.
- Session escape works as it does for RDP and VNC.

---

## 9. Properties pane

New settings for OSP:

| Group | Settings |
|---|---|
| Connection | Host, port, paired certificate (fingerprint, **Pair…**, **Forget**) |
| Video | Codec (Auto, AV1, HEVC, H.264), resolution (match client, or fixed), frame rate, bitrate (Auto or a cap), monitor |
| Input | Relative mouse (Auto, On, Off), controller passthrough, gamepad type to emulate |
| Audio | Where sound plays (here, on the host, both, off), output device, microphone (off or a device), push-to-talk key |
| Session | Clipboard, open in popout by default |

Credentials, Wake-on-LAN, host status and folder inheritance work as for every protocol. **Jump host is disabled**
for OSP, with a note: "UDP can't go through SSH tunnels; use a VPN to reach this host from outside its network."

---

## 10. Audio and clipboard

### Audio

Full audio both ways: the host's sound played on the client, and the client's microphone heard on the host.

**Sound from the host**
- **Capture:** WASAPI loopback of the host's output on Windows; a PipeWire monitor of the default sink on Linux.
- **Channels:** stereo, 5.1 and 7.1. The client says what its output device has; the host captures in that layout
  (setting its virtual speaker to match, below) and downmixes when it can't. Games that output surround keep it.
- **Encoding:** Opus at 48 kHz, 5 ms frames, multistream for surround, about 128 kbps for stereo and 256–512 kbps for
  7.1. Sent as datagrams with light FEC (Opus's in-band FEC plus our packet FEC), with packet-loss concealment for
  what still goes missing.
- **Latency and sync:** a small adaptive jitter buffer on the client (target 10–20 ms on a LAN). Audio and video
  carry the host's capture timestamps; the client keeps them within a frame of each other, nudging audio playback rate
  slightly rather than dropping or inserting sound.
- **Playback:** a low-latency output path. The current `IAudioOutput` (IronRDP's native output on CPAL) is tried first;
  if its buffering adds too much delay, OSP gets its own (WASAPI with `IAudioClient3` small periods on Windows,
  PipeWire on Linux, Core Audio on macOS). The client can pick an output device instead of the default.
- **Orrery features that already work for RDP keep working:** OSP implements `IAudioSession` and `IAudioTap`, so the
  tab's mute button, the "playing sound" indicator and session recording get the sound unchanged.
- **Where it plays**, per connection: **play here** (and silence the host's speakers while streaming, as Parsec
  does), **play on the host**, **both**, or **off**. That's RDP's `RemoteAudio` with two more choices; OSP gets its
  own setting so RDP's stays as it is.

**Microphone to the host**
- The client captures its chosen microphone (WASAPI, PipeWire, Core Audio), encodes Opus at 10–20 ms frames with
  in-band FEC, and sends it as datagrams. Off by default; turning it on asks for the OS's microphone permission where
  there is one (macOS, Wayland portals, Windows privacy settings).
- The host plays it into a **virtual microphone** that games, Discord and voice chat see as an input device.
  - Linux: a PipeWire virtual source, created and removed by the host. No driver needed.
  - Windows: a virtual audio device. That's another driver; it goes with phase 6's signed drivers, as one virtual
    audio driver providing both a speaker (below) and a microphone. Until then, the host can send the microphone to an
    existing virtual cable the user installed, chosen in its settings.
- Push-to-talk on a hotkey and a mute toggle in the overlay.
- Echo cancellation isn't ours to do: with "play here", the client's own OS and headset handle it, as with any voice
  chat.

**Headless hosts.** A Windows machine with no sound card has nothing to capture. The virtual speaker from the same
driver fixes that, and also lets the host match the client's channel layout exactly. Linux uses a PipeWire null sink.

**Host policy** per client (§6) covers sound (on or off) and the microphone (allowed or not).

### Clipboard

The same clipboard an RDP session has: **text, images and files, both ways.** The client side implements Orrery's
existing `IClipboardSession` (text, `ClipboardImage`, files, `ClipboardTransfer` progress and cancel), so the view's
clipboard code, the progress display and the connection's **Share clipboard** setting work unchanged.

| Content | Format on the wire | Notes |
|---|---|---|
| Text | UTF-8 | Same cap as RDP (`RdpClipboardBridge.MaxTextLength`, 1M characters) |
| Images | PNG | Converted from and to the local format (DIB on Windows, `image/png` on Wayland and macOS); capped in pixels and bytes |
| Files and folders | A file list, then each file's bytes on its own QUIC stream | Folders keep their structure; total size and count capped |

**How it flows**
- Each side watches its clipboard and sends an **offer** (what kinds it holds, sizes, file names) on the clipboard
  control stream. Nothing else moves until the other side pastes, or for text and small images, sends at once, as the
  RDP bridge does when the session gets the keyboard.
- **Files are fetched on their own streams**, so a large copy never holds up input, video or control messages. QUIC
  gives each stream its own flow control, so a 5 GB copy doesn't push frames out.
- **Received files are downloaded at once into a private folder**, as RDP does today, because the local clipboard can
  only hold files that exist. Progress and cancel use the existing `ClipboardTransfer` events. The folder lasts until
  the session ends or the next files arrive.
- Later, on Windows: an `IDataObject` with virtual files (`CFSTR_FILECONTENTS`) so a paste in Explorer pulls the
  bytes on demand instead of downloading first.

**Getting at the clipboard on each side**

| Where | How | Notes |
|---|---|---|
| Windows host | A clipboard listener in the user's session | A SYSTEM service in session 0 can't see the clipboard, so after phase 4 the per-session helper does this |
| Linux host (Wayland) | The **Clipboard portal** alongside the RemoteDesktop portal session (GNOME 46+, KDE); `ext-data-control-v1` where the compositor has it (KDE, wlroots) | Wayland doesn't let a background app read the clipboard any other way |
| Client, in a tab | Orrery's existing clipboard code | As RDP and VNC today |
| Client, in an SDL3 popout | SDL3's clipboard API, with MIME types | On Wayland only the focused window may read the clipboard, and while the stream window has focus Orrery's XWayland window doesn't |

**Safety.** Clipboard data from the other side is untrusted, as in `RdpClipboardBridge`: every size is capped, file
names are checked before they become paths (no `..`, no absolute paths, no device names, no reparse tricks), and
none of it is logged. Clipboard sharing needs both the connection's **Share clipboard** setting and the host's
policy for that client (§6), which can also limit it to **one direction** (host to client, or client to host) or to
text only.

---

## 11. Phases

Each phase ends usable and tested on real hardware, both GPUs where it applies.

1. **Spike, Windows to Windows.** WGC capture, NVENC, plain UDP, D3D11VA decode, a native swapchain in an Avalonia
   window. Measure glass-to-glass latency (a timer drawn on the host, filmed with the client next to it). Answers
   whether Avalonia can present fast enough. AMF follows straight after.
2. **Input.** Keyboard and mouse, relative mouse with pointer lock, SDL3 controllers on the client, virtual HID
   gamepad on the host.
3. **The real protocol.** QUIC over MsQuic, pairing, mutual TLS, FEC, rate control, recovery from loss, full audio
   (sound in stereo to 7.1, microphone to a PipeWire source or an existing virtual cable), the full clipboard (text,
   images and files both ways), and several monitors once one works (§5, §10). OSP appears in Orrery's tree and
   properties pane.
4. **The Windows service.** SYSTEM service plus a helper that follows the active desktop: works at the login screen and on
   UAC prompts, survives sign-out, runs unattended.
5. **Linux host, Wayland only.** Portal and PipeWire capture, VA-API encoding, uinput. Linux and macOS clients get
   their decode and present paths here too, the Linux one in an SDL3 Wayland window. A Wayland host can't stream the
   login screen or run before someone logs in: each session belongs to its compositor and the greeter is off limits.
   Linux hosts need a logged-in session (auto-login if unattended); there's no equivalent of phase 4.
6. **Signed drivers.** XInput-compatible gamepad, IddCx virtual display for headless machines, and a virtual audio
   device (speaker and microphone). Needs an EV
   certificate and Microsoft attestation signing through Partner Center ([code-signing.md](code-signing.md) covers the
   rest of signing). Tackled when we get there.

Later, maybe: a couch mode that opens a saved stream full screen in a popout and is driven by a controller; a
file-transfer panel beyond the clipboard.

---

## 12. Owner's answers (2026-11-03)

- **Own host or Sunshine?** Own host, for full control over scope.
- **License?** MIT, whatever we do. No GPL dependencies, no moonlight-common-c.
- **Transport?** Consider QUIC; adopted (§4).
- **Driver signing?** A real challenge, tackled when we reach it.
- **Merged or separate host?** Separate package from the same repo, same version (§2).
- **Gaming product or part of Orrery?** Client in Orrery, host as companion; no separate gaming client (§2).
- **Tab or popout?** Both; popout is being proposed in another session and becomes the main home for streams (§8).
- **Test hardware?** AMD and NVIDIA GPUs available.
- **Linux?** Wayland sessions only, host and client. Orrery's own window stays on XWayland; streams get a native
  Wayland window (§5).
- **Multi-display, HDR, VRR, 12-bit together?** All but 12-bit; 10-bit HDR is the ceiling (§5, display capabilities).
- **Clipboard?** (2026-11-04) The same as RDP: text, images and files, to and from the host (§10).
- **Audio?** (2026-11-04) Full audio: sound from the host including surround, and the microphone to the host (§10).

## 13. Open questions

- Default UDP port.
- Which PAKE and which library, after a license and maintenance check.
- SDL3 window or native surface in Avalonia for the popout on Windows and macOS (measure in phase 1).
- Whether the existing `IAudioOutput` is low-latency enough for streaming, or OSP needs its own output (measure in phase 3).
- Which Opus binding to ship (libopus is BSD; the .NET wrapper needs a license and maintenance check).
- Which SDL3 version supports HDR through Wayland's color-management protocol, and which compositors we test on.
- Whether input over a reliable stream adds measurable latency (measure in phase 3).
- Which AV1 encode and decode generations the two test GPUs are.
