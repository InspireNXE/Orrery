# Orrery roadmap

What's planned next and what's parked for later. Orrery stays free; nothing here is meant for a paid edition.

## Next feature pass

Nothing yet. Session recording and replay is built: see [design-session-recording.md](design-session-recording.md).
Find in a live terminal, saved commands and two tabs side by side are built (2026-11-04). Saved commands are only
sent when picked, never on connect, so they don't cover scripted connect below.
Commands in the Ctrl+K palette are built (2026-11-02): the current session's menu, every session or a folder's,
and a saved connection's actions, including copying its password. Opening a tunnel from it isn't: port forwards
belong to an SSH connection's settings.

## Parked for future releases

### Orrery Stream Protocol and Orrery Host

Low-latency desktop and game streaming in the spirit of Parsec, direct connect only: OSP as a protocol in the tree,
with GPU-encoded H.264, HEVC and AV1 over QUIC, controller passthrough and relative mouse, full audio (surround
sound from the host, microphone to it), the same clipboard as RDP (text, images and files, both ways), and Orrery
Host as a separate companion package on the machine being reached. Drafted, not started: see
[design-stream.md](design-stream.md).

### Browser access through a tray agent

A small local web server that shows the connection tree, so a phone or tablet on the same network can open a
session in a browser. The largest and riskiest idea here: it needs authentication, TLS, a browser-side RDP and VNC
client, and a careful look at what it exposes.

### Scripted connect

Run a script before or after connecting, or send keystrokes once logged in (mapping drives, starting an elevated
shell). Parked because scripts that run with the user's rights, triggered by a shared or imported connection file,
are a real risk. It would need explicit per-connection consent, no scripts in imported files, and a clear view of
what will run.
