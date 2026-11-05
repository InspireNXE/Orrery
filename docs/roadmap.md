# Orrery roadmap

What's planned next and what's parked for later. Orrery stays free; nothing here is meant for a paid edition.

## Built lately

- **0.1.2 previews 8 and 9 (2026-11-05):** Windows Hello asked before the master password; a remote desktop that
  isn't connected shown dimmed; RDP resizes about twice as fast, with the old picture filling the window meanwhile;
  RDP timings in the detailed log. Since then, on dev: RDP input sent at once and no longer held up by decoding;
  setup offers to add the shells it finds to the tree as LOCAL connections; RDP and VNC recordings keep the remote
  pointer's shape, and the live view shows it, in your own pointer scheme on Windows; a second file copy in an RDP
  session no longer fails; clicks at the very top of a full-screen session reach it; a portable data folder synced
  between computers takes in each one's changes to connections, the vault and settings while open, merging them
  rather than saving over them, with each computer's own paths, displays, window and recent connections kept apart;
  it says when Orrery is open on another one and when the sync app has made conflict
  copies, and each computer writes its own log file there.
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

Open from the feature table: a collapsed host showing its guests' sessions, and Touch ID, which needs a signed
macOS build.

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
