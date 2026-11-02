# Orrery roadmap

What's planned next and what's parked for later. Orrery stays free; nothing here is meant for a paid edition.

## Next feature pass

Nothing yet. Session recording and replay is built: see [design-session-recording.md](design-session-recording.md).

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
