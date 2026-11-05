# Orrery roadmap

What's planned next and what's parked for later. Orrery stays free; nothing here is meant for a paid edition.

## Built lately

- **0.1.2 previews (2026-11-04):** a Commands page in the side panel, with the editor in the right-hand pane and a
  right-click menu; saving a command on any connections at once (linked copies, with guests listed under their host);
  saved commands that send themselves when a saved connection connects, and ones that answer a password prompt;
  local terminals saved in the tree as LOCAL connections, and a local terminal as administrator on Windows;
  recordings that can include what's typed; names and addresses hidden in logs; update checks at every start and a
  What's new window; browser-style tabs with a list of every tab; panes that start the way they were left.
- **Earlier (2026-11-02 to 11-04):** session recording and replay (see
  [design-session-recording.md](design-session-recording.md)), find in a live terminal, two tabs side by side, and
  commands in the Ctrl+K palette: the current session's menu, every session or a folder's, and a saved connection's
  actions, including copying its password. Opening a tunnel from it isn't there: port forwards belong to an SSH
  connection's settings.

## Next feature pass

Suggested and not started; each waits for a go-ahead.

- **Environment variables for LOCAL connections**, set for the shell it starts.
- **Local shells offered at setup:** after the import step, *Add your local shells to the tree* lists the shells
  found (PowerShell, cmd, each WSL distribution; the login shell on Linux and macOS), ticked only when the tree is
  empty, and put in a LOCAL folder. Never added later, so a deleted one stays deleted.
- **Ctrl+click and Shift+click in *Save on*,** to pick several rows and tick them together, if *+ guests*, the
  filter and *Tick the N shown* turn out not to be enough.

Also open from the feature table: a collapsed host showing its guests' sessions, recording the remote pointer's
shape, and Touch ID, which needs a signed macOS build.

## Parked for future releases

### Browser access through a tray agent

A small local web server that shows the connection tree, so a phone or tablet on the same network can open a
session in a browser. The largest and riskiest idea here: it needs authentication, TLS, a browser-side RDP and VNC
client, and a careful look at what it exposes.

### Scripted connect

Run a program on this computer before or after connecting, such as mapping a drive. Sending text once logged in is
covered now: a saved command can send itself each time a saved connection connects, can answer a password prompt
(*Become root* for `sudo -i`), and imported commands never send by themselves until that's turned on. What's left is
running local programs, parked because programs that run with the user's rights, triggered by a shared or imported
connection file, are a real risk. It would need explicit per-connection consent, nothing that runs from imported
files, and a clear view of what will run.
