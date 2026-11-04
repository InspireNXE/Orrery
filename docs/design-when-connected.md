# Design: commands sent when connected

Status: phase 1 built on `feature/when-connected`; phase 2 designed, not built.

A user asked whether Orrery could, on connecting to an SSH host, run `sudo su` and answer its password prompt for
them. The answer builds on saved commands (README, *Saved commands*): phase 1 lets a saved command send itself once a
terminal connects; phase 2 adds waiting for what the server shows and answering a password prompt from a saved
credential, with "become root" as a ready-made sequence.

## 1. Phase 1: a saved command sent by itself

A saved command has **Send it by itself each time a saved connection connects** (`SavedCommand.WhenConnected`).

- **Where it applies.** Terminals of saved connections only: SSH, Telnet, serial. A local shell or a quick connect
  sends nothing by itself, even though both can send the defaults' commands by hand: a command meant for servers
  (`sudo -i`) has no business in a local PowerShell, and a typed-in address has no settings to say otherwise.
- **Order.** The defaults for all connections first, then each folder inwards, then the connection's own, each in the
  order it's listed. Set-up that's wider comes first, as a login profile's would.
- **Names.** As for commands sent by hand, a nearer command hides a further one of the same name. A hidden one isn't
  sent, whether or not the nearer one is, so a connection can switch a folder's command off for itself by saving one
  of the same name without the box ticked.
- **Once per tab.** Each tab sends them the first time its session connects. An automatic reconnect is a new tab, so
  it sends them again: it's a new shell. So does a session reopened at start-up.
- **Asking first.** One that asks first still asks, in the window the tab is in. Turning it down stops the rest, as
  does the session dropping: a later command may count on an earlier one (`cd` then `make`).
- **Typing ahead.** The commands are typed as soon as the session says it's connected, before the shell has drawn its
  prompt. A shell reads what was typed ahead in order, so this is fine for plain commands. It is not fine for anything
  that asks a question (`sudo` asking for a password reads the next command as the password): that's phase 2.
- **Import.** An imported command never sends itself, whatever the file says: it's kept, with *when connected* off and
  a warning saying so. A file from someone else shouldn't be able to run commands on your servers when you connect.
- **Schema.** A file with any command sent when connected is written as schema 5 (connections.json and Orrery
  exports), so a build that doesn't know the setting refuses it rather than loading it and dropping the setting on save.
- **Log.** "Sending N saved command(s) to X now that it's connected", then each one as for a command sent by hand, and
  where it stopped if it did. Never a command's text.

The connection pane marks such a command *when connected*.

## 2. Phase 2: waiting, and answering a password prompt

A password can't be a placeholder in a command. `{password}` in the text would put it on the visible command line, in
the shell's history and in recordings (which keep what the server sends back, and a command line is echoed), and a
command sent on a timer goes wrong whenever the prompt doesn't come: a sudo timestamp still valid, a NOPASSWD rule, the
first-use lecture. The password then lands at a shell prompt as a command.

So phase 2 adds steps that aren't commands, in a **When connected** list on a connection or folder:

- **Send**: a saved command, or text typed in place.
- **Wait for**: text or a pattern on the screen's last line, with a timeout (default: the shell's prompt).
- **Answer a password prompt**: with the connection's own login credential, or one chosen. Only straight after a
  *Wait for* that matched a password prompt.

Rules for answering:

1. Typed only when the last line matches a password prompt (`[sudo] password for riley:`, `Password:`, or a pattern
   given), within the timeout.
2. If the prompt doesn't come, nothing is typed. The sequence stops and says so in the tab: "sudo didn't ask for a
   password, so none was sent."
3. The password comes from a saved credential, never from the step. A locked vault asks to be unlocked, as at login.
4. A password prompt has echo off, so the answer isn't in the scrollback or a recording, as when typed by hand.
5. The log names the credential, never the password: "answered the sudo prompt for X with credential Y".

**Become root** is a ready-made sequence: send `sudo -i` (as `sudo su`, one process fewer), wait for a password prompt
or a root prompt (`#`), answer with the login's credential if it was a password prompt, wait for `#`. `su -` is the
same with root's credential chosen.

Also in phase 2:

- A way to connect without the steps: Ctrl+click Connect, or *Connect without its commands* on the menu.
- A marker on the tab of a session that became root.
- The same steps suit Telnet and serial logins (wait for `login:`, send the username, answer `Password:`).

SFTP has no shell, so files as root would be a feature of its own (starting `sftp-server` through sudo).

## 3. What we'd suggest instead

Settings should say that the safest root is set up on the server: a NOPASSWD sudoers rule for the commands needed, or
logging in as root with a key where that's allowed. Neither keeps a sudo password in Orrery. The password steps are
for servers where that can't be changed.
