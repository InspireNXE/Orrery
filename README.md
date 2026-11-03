<p align="center">
  <img src="src/Orrery.App/Assets/orrery.png" alt="" width="112">
</p>

<h1 align="center">Orrery</h1>

<p align="center">
  A remote connection manager for Windows, Linux and macOS.<br>
  RDP, SSH, VNC, Telnet and serial in one tree, opened in tabs, with passwords in your system's secret store.
</p>

<p align="center">
  <a href="https://github.com/InspireNXE/Orrery/actions/workflows/packages.yml"><img alt="Package build status" src="https://github.com/InspireNXE/Orrery/actions/workflows/packages.yml/badge.svg?branch=main"></a>
  <img alt="Version 0.1.0" src="https://img.shields.io/badge/version-0.1.0-5b6cff">
  <img alt="Windows, Linux and macOS" src="https://img.shields.io/badge/platforms-Windows%20%7C%20Linux%20%7C%20macOS-555">
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-512bd4">
  <img alt="MIT license" src="https://img.shields.io/badge/license-MIT-2e7d32">
</p>

<p align="center">
  <img src="docs/images/main-window.png" width="880"
       alt="Orrery's main window. On the left, the connection tree has a Production folder and a Lab folder, and in Lab a host called hv-01 with two guest machines under it. Each row has a mark for its session (web-01 connected, build-agent connecting, db-02 failed) and one for its host (most up, kiosk and win-test down, app-03 behind a jump host and not checked). In the middle, an SSH tab to web-01 shows a shell with systemctl, df and git output. On the right, the properties pane shows web-01's settings, with its protocol and username taken from the Production folder.">
</p>

| Runs on | Passwords | Accessibility | Imports |
| --- | --- | --- | --- |
| Windows, Linux and macOS, installed or portable | Credential Manager, macOS Keychain, Secret Service, or an encrypted vault file | Narrator, NVDA and JAWS; colors for color blindness | mRemoteNG `confCons.xml`, `.rdp` files, `~/.ssh/config` |

Orrery is built on Avalonia and .NET 10. It takes its cue from [mRemoteNG](https://mremoteng.org/) and imports its
`confCons.xml`, but isn't affiliated with that project.

> Version 0.1.0. The protocols work, but much hasn't been tried on real machines yet; see [Status](#status).

**Contents:** [Platform support](#platform-support) · [Features](#features) · [Accessibility](#accessibility) ·
[Keyboard shortcuts](#keyboard-shortcuts) · [Where your data lives](#where-your-data-lives) · [Building](#building) ·
[Status](#status) · [Security](#security) · [How it's made](#how-its-made) · [License and credits](#license-and-credits)

## Platform support

<table>
<tr><td>✅ Works</td><td>🧪 Built, not yet tried on a real machine</td><td>🔸 Partly, see the note</td><td>🗓️ Planned</td><td>➖ Not possible there</td></tr>
</table>

Orrery is built and used daily on Windows. The macOS and Linux builds pass the same tests but haven't run on a real
machine yet, hence the 🧪.

| | Windows | macOS | Linux (Wayland) |
| --- | :---: | :---: | :---: |
| **Connections** | | | |
| RDP | ✅ | 🧪 | 🧪 |
| SSH, with SFTP, port forwards and SOCKS | ✅ | 🧪 | 🧪 |
| SSH key files and agent keys | 🧪 | 🧪 | 🧪 |
| VNC | ✅ | 🧪 | 🧪 |
| Telnet | ✅ | 🧪 | 🧪 |
| Serial consoles | 🧪 | 🧪 | 🧪 |
| Local terminal | ✅ PowerShell, cmd, WSL | 🧪 login shell | 🧪 login shell |
| Web pages, in a tab or your browser | ✅ | 🧪 | 🧪 [^webkit] |
| Jump hosts for every protocol | ✅ | 🧪 | 🧪 |
| Quick connect, reconnect, Wake-on-LAN | ✅ | 🧪 | 🧪 |
| **Remote desktops** | | | |
| Fit the window and follow resizes | ✅ | 🧪 | 🧪 |
| Clipboard | ✅ text, images, files | 🧪 text, images, files [^files] | 🧪 text, images, files [^files] |
| Sound | ✅ | 🧪 | 🧪 |
| Mute a tab | ✅ | 🧪 | 🧪 |
| Shared drives and folders | 🧪 every drive or any folders | 🧪 home or any folders | 🧪 home or any folders |
| Full screen over several displays | 🧪 | ➖ one display [^spaces] | ➖ one display [^wayland] |
| System keys (Windows key, Alt+Tab) sent to the session | ✅ | 🔸 Send keys menu | 🔸 Send keys menu |
| Touch and pen, console session, broker redirection | 🧪 | 🧪 | 🧪 |
| **Recording** | | | |
| Record terminals and remote desktops, with sound | ✅ | 🧪 | 🧪 |
| Record web pages in a tab | ✅ | ➖ | ➖ |
| Play back, with speed, skip idle and Find | ✅ | 🧪 | 🧪 |
| Export `.cast` and `.avi` | 🧪 | 🧪 | 🧪 |
| Export MP4, with sound | 🧪 built in | 🔸 through ffmpeg | 🔸 through ffmpeg |
| MP4 without ffmpeg | ✅ | 🗓️ AVFoundation | ➖ |
| Record the remote pointer's shape | 🗓️ | 🗓️ | 🗓️ |
| **Credentials** | | | |
| System secret store | ✅ Credential Manager | 🧪 Keychain | 🧪 Secret Service |
| Encrypted vault file | ✅ | 🧪 | 🧪 |
| Unlock with biometrics | 🧪 Windows Hello | 🔸 Touch ID, needs a signed build | ➖ |
| **Organizing and import** | | | |
| Tree, folders, inheritance, host status | ✅ | 🧪 | 🧪 |
| Host groups (guests under their host) | ✅ | 🧪 | 🧪 |
| A collapsed host showing its guests' sessions | 🗓️ | 🗓️ | 🗓️ |
| Import mRemoteNG, `.rdp` and `~/.ssh/config`; export | ✅ | 🧪 | 🧪 |
| **Accessibility** | | | |
| Screen readers | ✅ Narrator, NVDA, JAWS | 🧪 VoiceOver | 🧪 Orca [^orca] |
| Color-blind colors and desktop correction | ✅ | 🧪 | 🧪 |
| **Packages** | | | |
| Installer | ✅ | 🔸 app bundle, not signed | 🧪 .deb, .rpm, AppImage |
| Portable | ✅ | 🧪 | 🧪 |
| arm64 | 🧪 | 🧪 Apple Silicon | 🧪 |

[^spaces]: macOS gives each display its own full-screen space.
[^wayland]: Wayland doesn't let a window place itself or span displays. X11 sessions aren't supported.
[^orca]: Avalonia 12 reads to screen readers through AT-SPI, under XWayland. Orca hasn't been tried with Orrery yet.
[^webkit]: Pages in a tab need WebKitGTK (libwebkit2gtk-4.1), which the .deb and .rpm recommend; without it they open in your browser. Like the rest of Orrery's window, they're drawn through XWayland.
[^files]: Remote files download when copied, up to 1 GB, into a private folder removed when the session ends.

Further out: browser access through a tray agent, and scripted connect. See the
[roadmap](docs/roadmap.md).

## Features

### Protocols

| Protocol | Library | Notes |
| --- | --- | --- |
| RDP | [IronRDP](https://github.com/Devolutions/IronRDP) | Follows window resizes or keeps a fixed size; sound; shared drives; full screen over chosen displays (Windows); console session; touch and pen; broker redirection; opens `.rdp` files |
| SSH | [SSH.NET](https://github.com/sshnet/SSH.NET) | Port forwards (`L`, `R`, `D`), a folder-wide SOCKS proxy, key files, agent keys, keep-alives, SFTP browser |
| VNC | [MarcusW.VncClient](https://github.com/MarcusWichelmann/MarcusW.VncClient) | Text clipboard, view only, fill, shrink to fit or actual size |
| Telnet | Built in | |
| Serial | [System.IO.Ports](https://www.nuget.org/packages/System.IO.Ports) | COM ports and `/dev/tty*`, with speed, data bits, parity, stop bits and flow control |
| Web (HTTP, HTTPS) | [Avalonia.Controls.WebView](https://www.nuget.org/packages/Avalonia.Controls.WebView): WebView2, WebKit or WebKitGTK | Keeps admin pages in the tree beside the rest; opens in a tab, or your default browser if you choose |
| Local terminal | Built in | PowerShell, cmd or WSL on Windows; your login shell on Linux and macOS |

### Connecting

<p align="center">
  <img src="docs/images/quick-connect.png" width="720"
       alt="Quick connect open over the dimmed main window, with web typed in. It lists the open web-01 session first with Switch to, then the saved connections web-01 and web-02 from Production, then RDP to web and SSH to web to connect directly, and an action to make a new connection named web. A footer lists the keys: arrows to move, Enter to open, Alt+Enter for settings, Esc to close, > for commands, and ssh://, rdp://, vnc:// or telnet:// to pick a protocol.">
</p>

Quick connect (Ctrl+K) searches saved connections and open sessions as you type, or connects straight to a host.
It runs commands too: start with `>` to list them all, or type a few words ("disc prod", "copy pass web").

- **Commands** in quick connect: everything in the current session's menu (Send keys, Save as PDF, Start
  recording…), reconnect or disconnect every session, connect or disconnect everything in a folder, and for a saved
  connection edit, wake, open its management page, browse its files, or copy its address or password. A copied
  password is cleared from the clipboard after 30 seconds, and on Windows kept out of clipboard history.

- **Jump hosts** work for every protocol, like `ssh -J`; RDP, VNC and Telnet go through an SSH tunnel. Web pages
  can't, in a tab or the browser: give the jump host a SOCKS proxy instead. The tunnel's port on this computer takes
  only Orrery's own connection, so another user of a shared machine can't ride it.
- **Folders** can give every SSH connection inside a SOCKS proxy (like its own `D 1080`) and a keep-alive interval
  (30 seconds by default; 0 for none).
- **Quick connect** also takes `http://` and `https://` addresses, which open in a tab like other web pages, and serial ports:
  `COM3`, `/dev/ttyUSB0` or `serial://<port>`.
- **`.rdp` files** open from the tree's right-click menu or the command line (`Orrery server.rdp`) without being
  saved; Import adds them to the tree. Saved passwords aren't imported (Windows encrypts them per user and
  computer), so Orrery asks. With Orrery already running, a second start says so; use Open in that window.
- **Ctrl+Alt+End** sends Ctrl+Alt+Del (Ctrl+Cmd+End on macOS). The Send keys menu has it and other keys a remote
  desktop may need; View only stops sending keys and clicks.
- **Touch and pen** pass through RDP, with pressure, tilt and the eraser, when the server takes them.
- **Sound** plays here unless you turn it off, in which case the server isn't asked for any. Windows servers only
  send sound with the drive-sharing channel open, so Orrery opens it, empty, when nothing is shared.
- **Muting:** a tab playing sound shows a speaker, as in a browser. Click it, or *Mute tab* in the tab's menu.
  Recordings still get the sound.
- **Graphics pipeline (RDPGFX)** is used when the server offers it, without H.264, which blurred text. Resizing,
  full screen included, then keeps the session running. Without it, each resize restarts the display.
- **Shared drives:** every drive, or folders you pick, appear as drives on the remote computer, one each. On Linux
  and macOS, sharing every drive shares your home folder. Orrery says when a folder isn't shared.
- **Full screen** goes where the connection says, under Display › Full screen on: the window's display, every
  display, or ones you pick on a map. On Windows, a remote desktop gets one monitor per display; with a gap (1 and
  3), the remote monitors sit side by side and display 2 keeps showing this computer. VNC stretches across.
  `.rdp` files' `use multimon` and `selectedmonitors` are read, numbered as mstsc numbers them.
- **Console session**, as `mstsc /admin`.
- **RD Connection Broker** redirections are followed (up to five) with the routing token sent back.
- **SSH keys:** a connection or folder can name a key file. Orrery tries a saved key, then the file, then your
  agent, then the password. The file is read on each connect, never copied. Passphrases are asked for and can be
  saved. On Linux and macOS, Orrery warns when others can read the key.
- **SFTP browser** beside the terminal: upload, drop in, download, rename, delete, new folder.
- **Clipboard** is shared unless turned off for a connection or folder. RDP carries text, images and files; VNC,
  text. On Linux and macOS, remote files download when copied (up to 1 GB), with progress and Cancel at the foot of
  the desktop. Not yet tried on a real Mac or Linux machine.
- **When a connection fails or drops,** the tab says what happened and what to try. Enter reconnects; details are a
  click away, with a Copy button.
- **Reconnects** happen on their own, waiting longer each time (up to a minute), and at once when the network
  returns. A refused login or an untrusted host key or certificate is never retried.
- **Wake-on-LAN** from the right-click menu, once a connection has a MAC address.
- **Management page:** give a connection a URL, such as a firewall's admin page or an out-of-band console, and
  *Open management page* opens it in a tab. Folders don't pass it on. Web pages go to your default browser instead if you choose that in Settings, or for one connection or folder.
- **Web tabs** have back, forward and reload, and Print and Save as PDF under More. Links meant for a new window open
  in a tab beside the page; a page's own pop-up, such as a sign-in, opens as a pop-up. An untrusted certificate shows
  the engine's warning, with a way past it, and the tab says the page didn't load. The lock before the address is
  closed only for HTTPS (and SSH and RDP); plain HTTP, Telnet and VNC show it open, in amber. A page opens only web
  addresses in new tabs, a few at a time, and Open in browser takes only web addresses. Their sign-ins and cookies
  stay in `web/` in the data folder; Settings > Web pages clears them, and Move data leaves them behind.

<p align="center">
  <img src="docs/images/connection-lost.png" width="720"
       alt="A tab for db-02 whose connection failed. In the middle of the tab, a panel headed Login refused says db-02.prod.example didn't accept the login, to check the username and password, or the key, then reconnect, and that this needs you, so Orrery won't try again by itself. Below are Reconnect and Copy details buttons and an open Details section with the time and the server's message. The properties pane on the right says db-02 last connected 1 week ago and that the last attempt failed just now.">
</p>

### Organizing

- A tree with folders, drag and drop, multi-select and a filter. Connections inherit settings from their folders.
- Host groups put a host's guest machines under it.
- **Two marks per row.** Before the name, the session: filled dot connected, broken ring connecting, cross failed,
  empty ring none. Beside the protocol, the host: filled dot up, cross down, dashed ring can't be checked. Hover for
  words.
- **Host checks** run about once a minute, only for visible connections and never through a jump host. They're a
  plain TCP connect by default, which some networks treat as a port scan; Settings can switch to ping or turn them
  off. Firewalls often block ping, so up hosts may show as down; without ping rights, hosts show the dashed ring.
  Serial ports aren't checked; web pages are checked at their host and port. A folder or connection can turn its
  checks off, and an import asks first whether to check the hosts it adds.
- **Properties pane:** one line per setting. A blue dot marks values set here; muted ones are inherited. Click to
  change, or "Use that instead" to inherit again. The footer counts what's set here.
- **This session tab:** while a remote desktop is open, Scaling, View only and Color correction change it at once,
  until it closes; Keep for next time saves the change. Settings that need a reconnect are listed with a Reconnect
  button.
- **Pane layouts** (Settings › General, or the pin in each pane's header), for the side panel and the properties
  pane: Pinned docks it; Over the session leaves a strip and opens over the sessions; Hidden leaves no strip (the
  window's edge or the shortcut brings it back). A pane opened over a session doesn't resize the remote desktop; on
  Linux and macOS it opens beside a web page, since nothing can be drawn over the page there. Drag a pane's inner edge to resize it.
- **Last connected** shows in the properties pane ("3 days ago", exact time on hover), with a newer failed attempt
  if there was one.

### Credentials, import and export

- Passwords go in Windows Credential Manager, the macOS Keychain or the Linux Secret Service, or an encrypted vault
  file (Argon2id, XChaCha20-Poly1305) where there's no store or you prefer one.
- Windows Hello can unlock the vault when its key is held in the TPM (not over Remote Desktop). Touch ID is written
  but needs a signed build. See [the design notes](docs/design-biometric-unlock.md).
- **Import:** mRemoteNG `confCons.xml`, Orrery `.json`, `.rdp` files, and `~/.ssh/config` with `Include` files,
  `Host *` defaults and `ProxyJump` chains (as jump hosts). Nothing in an SSH config runs, and key files aren't
  opened. The preview names every connection that shares drives or folders, or opens a port forward or proxy beyond
  this computer, so a file from someone else can't set those up unseen.
- **Export:** Orrery `.json`, mRemoteNG `confCons.xml` or CSV. Passwords are encrypted under a password you choose,
  at least 12 characters and not a common one (as for a new vault); `confCons.xml` uses 50,000 key rounds, the most
  mRemoteNG offers. CSV never gets them. Serial connections are left out of `confCons.xml`, which has none, and the export says so.
- The credential editor hides a private key until you press Show; pasting still works.

### Display and color

- **Display quality** per connection or folder: Balanced, Best look or Fastest.
- **Enlarge to match display scaling** (on by default): RDP tells the server your scaling, as Windows' client does,
  so it draws at full resolution with text sized like your local apps. VNC can't be told, so its picture is enlarged
  here. Off, one remote pixel is one screen pixel.
- **Scaling** for a desktop that doesn't match its tab: *Fit the tab* (default), *Shrink to fit*, or *Actual size*
  with scroll bars.
- **Interface colors:** Standard, Clearer (easier to tell apart for most color blindness) and Color-blind
  (Okabe-Ito). The terminal has its own color-blind scheme.
- **Color vision:** pick protanopia, deuteranopia or tritanopia, and Orrery switches to color-blind colors, tunes the
  terminal, and shifts the colors you'd confuse in RDP, VNC and web pages, at a strength you pick. A connection or
  folder can turn that off for design or photo work.
- Session states, host status, warnings and errors each have a shape, icon or word as well as a color.
- HDR isn't possible over RDP or VNC.

<p align="center">
  <img src="docs/images/settings-appearance.png" width="720"
       alt="Settings open on the Appearance page. The Color vision card has Deuteranopia: green-weak chosen and the correction strength slider at 80%. The Interface colors card offers Standard, Clearer and Color-blind (Okabe-Ito), with Color-blind selected. The Terminal colors card below it is set to Color-blind friendly.">
</p>

### Recording sessions

<p align="center">
  <img src="docs/images/recording.png" width="720"
       alt="The web-01 SSH tab being recorded. The tab has a red dot beside its name, and the session toolbar has a red REC pill reading 0:10. The terminal shows a deploy script running: fetching, installing, building, linking and reloading nginx, then a health check and the last lines of the access log.">
</p>

- **What's recorded:** what a terminal printed (never what's typed), or what a remote desktop showed, at up to 60
  frames a second, taken only when the screen changes, with RDP sound. On Windows, a web page in a tab is recorded
  the same way, from the engine's own picture of the page (no sound); WebKit on Linux and macOS can't give one.
- **Starting:** the *Record sessions* setting (inherited, so a folder can record everything in it), *Record every
  session* in Settings, or the tab's menu. A red dot on the tab and a *REC* pill show it's on; the pill changes the
  frame rate and sound, or stops. A reconnect carries on in the same file.
- **Encrypted,** sealed to a key kept with your saved credentials: recording never asks for the vault, playing does,
  and locking the vault closes the player. Recordings live in `recordings/` in the data folder, readable only by
  you; old ones are deleted by age and total size.
- **Playback** in the *Recordings* panel: ½× to 8×, sound at its pitch up to 4×, Skip idle, Loop, 10-second skips,
  markers for drops and reconnects, volume, Find for terminals with its matches on the timeline, keyboard shortcuts
  (? lists them), and your color correction.
- **Export** an unencrypted copy: `.cast` for asciinema, Motion-JPEG `.avi`, or MP4 (H.264, H.265 or AV1). On
  Windows, MP4 uses Windows' own encoders; elsewhere, or for a codec Windows lacks, it uses an ffmpeg you've chosen
  in Settings. Orrery never downloads or bundles ffmpeg. See
  [the design notes](docs/design-session-recording.md).

<p align="center">
  <img src="docs/images/recording-playback.png" width="720"
       alt="The Recordings section. The side panel lists one recording, web-01, SSH, 11 seconds, 1 KB. The player shows that recording paused at 0:08 of 0:11, with the deploy output on screen, Find in output and Export at the top, and play, 10-second skips, Skip idle, Loop and a 1× speed menu at the bottom.">
</p>

### Your workspace

- Install it, or run it portable with its data wherever you choose.
- Orrery remembers the window and open tabs and can reopen them at start: ask, always or never. Quick-connect hosts
  come back by address and ask for their login again. Never also stops saving the tab list and deletes the saved
  one; window size, position and panes are still kept.
- Turning off *Remember recent connections* deletes the Recent list and last-connected times, and records nothing
  until it's back on.
- Dark first.

## Accessibility

Orrery aims to work with a screen reader, from the keyboard, and with color blindness. Known gaps are at the end.

### Screen readers

Narrator, NVDA and JAWS on Windows.

- Every button, field and list says what it is.
- Tree rows give their name, what they are (protocol and host, or a folder's count), session state, and whether the
  host is up.
- Session tabs give their connection, state, and which is current.
- When a connection fails or drops, you hear why and the focus moves to Reconnect.
- Prompts to trust a new host key or certificate take the focus and are read out.
- Terminals read new output a few lines at a time once it pauses, without echoing your typing. On while a screen
  reader runs; change it under Settings › Appearance › Accessibility. The whole screen and the cursor's position can
  be read too.
- Password fields give a dot per character, never the password. The private key box says only "Private key, 3
  lines".

### Keyboard

- Terminals and remote desktops keep every key, Tab included.
- Ctrl+Alt+Home (Ctrl+Cmd+Home on macOS) leaves the session for its tab, leaving full screen first; Enter goes back.
- In a terminal, Ctrl+Shift+A selects all; plain Ctrl+A still reaches the shell.

### Color

- See [Display and color](#display-and-color) for the color sets, terminal scheme and desktop correction.
- Tests check every meaningful color against simulated protanopia, deuteranopia and tritanopia (Machado et al.
  2009) for distance (CIEDE2000), and against WCAG contrast.

<p align="center">
  <img src="docs/images/interface-colors.png" width="474"
       alt="The same connection tree twice, in Standard and in Clearer interface colors. In Standard, connected and up marks are green and down marks are a pinkish red. In Clearer, the up marks are teal and the down crosses a brighter red. The connecting mark is a yellow broken ring in both.">
</p>

### Not there yet

- A remote desktop is a picture: a screen reader hears only "Remote desktop:" and the connection's name.
- Color correction separates confusable colors but can't show ones you can't see, and changes how pictures look.
  It's been checked by numbers, not yet by color-blind people.
- Avalonia doesn't let screen readers move through a terminal by line or word yet; they get one block of text.
- Tree rows don't report their level or "2 of 5". Password fields aren't marked as such, so a reader that echoes
  typing will read a password aloud; turn that echo off while typing one.
- VoiceOver is untested. On Linux, Avalonia 12 reads to screen readers through AT-SPI, but Orca hasn't been tried
  with Orrery yet.

## Keyboard shortcuts

On macOS, Cmd takes the place of Ctrl.

| Keys | Action |
| --- | --- |
| Ctrl+K | Quick connect; `>` for commands |
| Ctrl+Shift+T | Local terminal |
| Ctrl+Shift+A, Ctrl+Shift+C, Ctrl+Shift+V (Cmd+A, Cmd+C, Cmd+V on macOS) | In a terminal: select all, copy, paste |
| Ctrl+, | Settings |
| Ctrl+B | Show or hide the side panel, over the sessions unless it's pinned; the mode stays as it is |
| Ctrl+Alt+B | Show or hide the properties pane, the same way |
| Ctrl+Alt+Enter (Ctrl+Cmd+F on macOS) | Full screen (changeable in Settings) |
| Ctrl+Alt+Home (Ctrl+Cmd+Home on macOS) | Leave the session for its tab; Enter goes back |
| Ctrl+Alt+End (Ctrl+Cmd+End on macOS) | In a remote desktop: send Ctrl+Alt+Del |
| Enter | Connect the selected connections |
| F2 | Rename |
| Ctrl+D | Duplicate |
| Delete | Delete the selected rows |

A focused remote session gets keys first, so these go to the remote machine. Full screen, Ctrl+Alt+Home and
Ctrl+Alt+End always reach Orrery. Ctrl+Shift+T works from a terminal, not from a remote desktop.

## Where your data lives

A portable copy has `orrery.portable.json` beside the executable and asks on first launch where to keep its data.
An installed copy uses each system's usual folder.

<details>
<summary>Data folders for an installed copy</summary>

| | Installed copy |
| --- | --- |
| Windows | `%APPDATA%\Orrery` (cache and logs in `%LOCALAPPDATA%\Orrery`) |
| macOS | `~/Library/Application Support/Orrery` |
| Linux | `~/.config/orrery` (XDG directories are respected), for the .deb, .rpm and AppImage |

</details>

`history.json` keeps when each connection last connected or failed, by connection, so it survives renames and
moves. It stays on this computer: exports leave it out, and deleting a connection deletes its entry.

Recordings are in `recordings/` in the data folder, encrypted. Move data takes them along.

<details>
<summary>Logs</summary>

One file a day, kept two weeks, in `%LOCALAPPDATA%\Orrery\Logs`, `~/Library/Logs/Orrery` or
`~/.local/state/orrery/logs` (portable: the data folder). They record what connected where and what went wrong,
never passwords, keys, keystrokes or screen contents. Settings opens the folder, and has a detailed logging switch
that turns itself off when Orrery closes.

</details>

## Building

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) and PowerShell (5.1 on Windows, or
[PowerShell 7](https://github.com/PowerShell/PowerShell) on Linux and macOS). From the repo root:

```powershell
./build/Get-IronRdp.ps1
dotnet build Orrery.sln
dotnet test Orrery.sln
dotnet run --project src/Orrery.App
```

### The IronRDP package

Several RDP features (the Linux and macOS clipboard, sound, shared drives, the console session, spanning displays,
broker routing) need unreleased changes to IronRDP's .NET binding. Until they ship, Orrery uses a
`Devolutions.IronRdp` built from a fork, at the `IronRdpVersion` in `Directory.Build.props`. It isn't on nuget.org:
`build/Get-IronRdp.ps1` downloads it from the fork's releases into `.nuget-local` (git-ignored), and refuses a file
whose SHA-256 doesn't match `build/ironrdp-package.json`. A restore failing with NU1301 or NU1101 for
`ironrdp-local` means the script hasn't run yet. Run it again only when `IronRdpVersion` changes.

<details>
<summary>Using a package you built, or going back to the released one</summary>

For your own build: `./build/Get-IronRdp.ps1 -FromFolder <folder> -Sha256 <hash>`. Its content hash differs from the
lock files', so locked restores fail until `./build/Update-IronRdpLockHashes.ps1` updates it (checking nothing else
changed). The published package is built for all six platforms by the fork's own workflow; see
[docs/packaging.md](docs/packaging.md).

To return to the released package, remove the `ironrdp-local` source and its mapping from `nuget.config`, set
`IronRdpVersion` to `2026.10.29`, and run `dotnet restore Orrery.sln --force-evaluate`.

</details>

### Packages

To build everything this machine can:

```powershell
./build/Build-All.ps1
```

It runs the scripts below, skipping (with a warning) any whose tools are missing, then lists what it built. Windows
builds everything but the AppImage. `-NfpmPath` and `-IsccPath` point at tools not on PATH; `-SkipPortable`,
`-SkipInstaller` and `-SkipLinux` leave steps out.

<details>
<summary>What each script builds, and what it needs</summary>

| Script | Builds | Needs |
| --- | --- | --- |
| `./build/package.ps1` | Portable zips for win-x64, win-arm64, linux-x64, linux-arm64, macos-arm64 and macos-x64 (`-Runtimes win-x64` for one; .NET calls the Mac ones `osx-arm64` and `osx-x64`) | .NET SDK |
| `build\Build-Installer.ps1` | Windows installer | Windows, [Inno Setup](https://jrsoftware.org/isinfo.php) 7 |
| `./build/Build-LinuxPackages.ps1` | .deb and .rpm, x64 and arm64 | [nfpm](https://nfpm.goreleaser.com/install/) (runs on Windows too) |
| | AppImage, x64 and arm64 | Linux, [appimagetool](https://github.com/AppImage/appimagetool/releases) |

</details>

Packages are self-contained; no .NET needed on the target. Output goes to `artifacts/`. On Windows and Linux the
portable app is one executable that unpacks its native libraries to a cache folder on first run. On macOS it's an
`Orrery.app` bundle, not yet signed or notarized. On Linux, `chmod +x Orrery` after unzipping; the machine needs what
[Linux](#linux-wayland-only) lists, which the .deb and .rpm pull in. No Flatpak.

GitHub Actions builds all of these on every push and pull request. [docs/packaging.md](docs/packaging.md) covers
Linux requirements, third-party notices, lock files and the workflow.

### Screenshots

`tools/Orrery.Screenshots` draws this README's screenshots from made-up data:
`dotnet run --project tools/Orrery.Screenshots`. See [docs/screenshots.md](docs/screenshots.md).

### Repository layout

<details>
<summary>What's in each folder</summary>

| Path | What's in it |
| --- | --- |
| `src/Orrery.App` | The Avalonia app: windows, connection tree, properties pane, settings |
| `src/Orrery.Core` | Model, storage, credentials, vault, import and export |
| `src/Orrery.Protocols.*` | RDP, SSH, VNC, Telnet and serial sessions, and local terminals |
| `tests/` | xUnit tests, including headless Avalonia UI tests |
| `tools/Orrery.Screenshots` | Draws the README's screenshots from made-up data |
| `build/` | Packaging, installer and third-party notice scripts |
| `installer/` | Inno Setup script for Windows; `installer/linux/` has the Linux launcher and icons |
| `docs/` | Design notes, the packaging guide, and the README's images in `docs/images` |
| `.github/` | The GitHub Actions workflow that builds the packages |

</details>

## Status

On Windows, RDP resizing, the clipboard and the installer work. The RDP clipboard Linux and macOS use was tested for
text against Windows Server 2019; its images and files haven't met a real server yet. Below: what each system needs,
what differs there, and what's untried.

### Every platform

IronRDP versions needed: `2026.10.29-orrery.3` for sound, shared drives, the console session, spanning displays and
the routing token; `orrery.4` for sound without shared drives; `orrery.6` for the graphics pipeline and display
scaling; `orrery.7` for recording sound; `orrery.8` for tab muting; `orrery.9` for clipboard files off Windows;
`orrery.10` for several shared folders off Windows. With a package built on one machine, whose other platforms'
libraries are older, Orrery connects without the missing features and logs which. IronRDP is built without Opus (it needs CMake), so servers send PCM, ADPCM or G.711 (or AAC,
on Windows).

Not yet checked on real machines:

- VNC authentication
- SSH key files, with and without a passphrase: direct, in the file browser and through a jump host
- How each protocol reports real network failures (an RDP server refusing a login, especially)
- RDP touch and pen
- A real RD Connection Broker farm (tested against made-up redirections)
- RDP shared drives and the console session
- A serial console on real hardware (tested against a stand-in port)
- SSH keep-alives and a folder's SOCKS proxy against a real server
- `.rdp` files saved by Remote Desktop Connection itself (tested against files matching its format)
- Recording exports (AVI, MP4) in other players

### Windows

- **Needs:** nothing; packages are self-contained.
- **Keys:** while a session has the keyboard, Orrery takes the Windows keys for it, as mstsc does. Windows keeps
  Ctrl+Alt+Del, so Ctrl+Alt+End sends it.
- **Shared drives:** every drive, or any folders, each as its own drive.
- **Full screen** spans every display or chosen ones, gaps included.
- **Sound** can also come as AAC.
- **Not yet checked:** full screen over several displays, especially with mixed scaling; the Windows Hello prompt;
  the win-arm64 build.
- Executables aren't code-signed, so SmartScreen may warn on first launch, and antivirus may flag the single-file
  executable.

### macOS

- **Needs:** nothing. The app isn't signed or notarized, so Gatekeeper asks on first launch. Serial ports are
  `/dev/cu.*` and `/dev/tty.*`.
- **Keys:** Cmd+Tab, Cmd+Space and other macOS shortcuts stay with macOS. The Send keys menu sends Alt+Tab, the
  Windows key and the rest.
- **Shared drives:** your home folder when sharing every drive, or any folders, each as its own drive.
- **Full screen** is on one display, since macOS gives each its own space. A connection can pick which; Orrery says
  so when it's set to several.
- **RDP clipboard:** text, images and files. Remote files download when copied (up to 1 GB), not when pasted.
- **Not yet checked:** all of it on a real Mac, including local terminals, the RDP clipboard, Touch ID (needs a
  signed build) and VoiceOver.

### Linux (Wayland only)

Wayland sessions only. Avalonia 12.1's Wayland backend is experimental, so Orrery runs under XWayland, but it has no
X11-specific code. X11 sessions aren't supported.

- **Needs:** XWayland, fontconfig and ICU, which most desktops have. The ALSA library (`libasound.so.2`, from
  `libasound2t64` or `libasound2` on Debian and Ubuntu, `alsa-lib` on Fedora): IronRDP links it, so RDP can't connect
  without it. The .deb and .rpm pull these in; the portable zip and AppImage don't. For the system keyring,
  libsecret and a Secret Service (GNOME Keyring or KWallet); otherwise, or when portable, the vault file is used.
  For serial ports, your user must be in `dialout` (`uucp` on Arch); log out and back in after adding it.
- **Keys:** Super, Alt+Tab and other desktop shortcuts stay with the desktop. The Send keys menu sends them.
- **Shared drives:** your home folder when sharing every drive, or any folders, each as its own drive.
- **Full screen** is on the window's display: Wayland doesn't let windows place themselves or span displays. Orrery
  says so when a connection is set to several.
- **RDP clipboard:** text, images and files. Remote files download when copied (up to 1 GB), not when pasted.
- **Screen readers:** Avalonia 12 reads to screen readers through AT-SPI, but Orca hasn't been tried with Orrery yet.
- **Not yet checked:** all of it on a real machine: the .deb, .rpm and AppImage, arm64, local terminals, the RDP
  clipboard, sound, a serial device and the Secret Service.

## Security

Report a vulnerability privately; see [SECURITY.md](SECURITY.md). Two audits so far, with their fixes:
[October 2026](docs/audit/2026-10) and [November 2026](docs/audit/2026-11), which covered web tabs, recording, the RDP
clipboard and shared folders, imports and the build.

## How it's made

Orrery is built with AI assistance. Its code, tests and documentation are written with
[Claude Code](https://claude.com/claude-code), Anthropic's coding agent, under the maintainer's direction: they
decide what goes in and try it out. Every commit says so with a `Co-Authored-By: Claude`
trailer. The security audits above were run the same way.

## License and credits

Orrery is MIT-licensed, © 2026 InspireNXE. See [LICENSE](LICENSE).

Built on open-source work, all under permissive licenses (MIT, Apache 2.0, BSD, ISC, OFL):

- [Avalonia](https://avaloniaui.net/) (UI), [SkiaSharp](https://github.com/mono/SkiaSharp) and
  [HarfBuzzSharp](https://github.com/mono/SkiaSharp) (drawing and text), and the [Inter](https://rsms.me/inter/) font
- [IronRDP](https://github.com/Devolutions/IronRDP) by Devolutions (RDP), [SSH.NET](https://github.com/sshnet/SSH.NET)
  (SSH), [MarcusW.VncClient](https://github.com/MarcusWichelmann/MarcusW.VncClient) (VNC, through the
  [community fork](https://github.com/karbonbaron/MarcusW.VncClient))
- [NSec](https://nsec.rocks/) and [libsodium](https://libsodium.org/) (vault and export encryption),
  [BouncyCastle](https://www.bouncycastle.org/) (mRemoteNG file encryption)
- [.NET](https://dotnet.microsoft.com/) by Microsoft and the .NET Foundation, which ships inside every package
- Portions of this software are copyright © The FreeType Project (https://freetype.org). All rights reserved.
  (FreeType is built into Skia.)

Every package carries `THIRD-PARTY-NOTICES.txt`, generated from what that build ships, with each component's license
and copyright notices. The AppImage also carries `APPIMAGE-RUNTIME-NOTICES.txt` for the AppImage runtime and the
libraries linked into it, including libfuse (LGPL 2.1).

mRemoteNG is GPL-licensed. Orrery reads and writes `confCons.xml` with code written from the file format alone, and
contains none of mRemoteNG's code.
