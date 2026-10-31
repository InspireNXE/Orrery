# Orrery roadmap

What's planned next and what's parked for later. Orrery stays free; nothing here is meant for a paid edition.

## Next feature pass

### Session recording and replay

Record sessions, optionally, to replay later for audits or for "what did I do on that box".

- SSH and Telnet as text, in [asciinema](https://docs.asciinema.org/manual/asciicast/v2/) format: searchable and
  small. The terminal already sees every byte, so recording is a tee of `DataReceived` with timestamps.
- RDP and VNC as video, encoded from the frame buffer the view already has, at a low frame rate with frames
  skipped when nothing changed.
- Turned on per connection (an inheritable setting) or per session from the tab menu, with a visible indicator
  while recording.
- Stored under the data folder (`recordings/`), optionally encrypted with the vault's key, with a retention limit.
- A player for both kinds in Orrery itself, so recordings don't need other software.

## Parked for future releases

### Command palette with actions

Ctrl+K already finds connections. It could also run actions: "reconnect all", "open a tunnel to db:5432",
"copy the password for x", "disconnect everything in Production". Needs an action registry the menus share.

### Browser access through a tray agent

A small local web server that shows the connection tree, so a phone or tablet on the same network can open a
session in a browser. The largest and riskiest idea here: it needs authentication, TLS, a browser-side RDP and VNC
client, and a careful look at what it exposes.

### Scripted connect

Run a script before or after connecting, or send keystrokes once logged in (mapping drives, starting an elevated
shell). Parked because scripts that run with the user's rights, triggered by a shared or imported connection file,
are a real risk. It would need explicit per-connection consent, no scripts in imported files, and a clear view of
what will run.
