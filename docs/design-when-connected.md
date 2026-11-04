# Design: commands sent when connected

Status: phases 1 and 2 built on `feature/when-connected`; the later items at the end of section 2 aren't.

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

The first design had a **When connected** list of steps (send, wait for, answer) apart from saved commands. What's
built is smaller: the waits are built in, and answering is a setting of the command, so there's one list to keep.

- **Waiting for the shell.** Each command sent when connected waits for the shell's prompt first (output stopped for
  300 ms, and the cursor's line ending in `$`, `#`, `%`, `>` or `❯`), up to 15 seconds, then is typed anyway, as a
  shell reads what's typed ahead. A password prompt showing instead stops the rest: nothing is typed into it.
- **Answering** (`SavedCommand.Password`, *If it asks for a password* in the editor): leave it, the connection's login
  password, or a chosen credential's (`PasswordCredentialId`). Only for a command that presses Enter. It applies
  however the command is sent, by hand or when connected.

Rules for answering:

1. Typed only when, within 10 seconds of sending, output stops on a password prompt (`[sudo] password for riley:`,
   `Password:`, `riley@host's password:`, a key's passphrase). Not passwd's `New password:` or `Retype…`.
2. A shell prompt instead (sudo didn't ask) is fine: nothing is typed and the next command goes. No prompt at all
   types nothing and stops the rest, saying so in the tab.
3. The password comes from a saved credential, never from the command. A locked vault asks to be unlocked, as at
   login, and the prompt has to be the one still showing afterwards.
4. Typed as keys, not pasted, and not noted for the screen reader's echo matching. A password prompt has echo off, so
   it isn't in the scrollback or a recording, as when typed by hand.
5. Typed once. If the password prompt comes back, it isn't typed again: the tab says the password wasn't taken.
6. The log names the command, the connection and the credential, never the password.
7. An imported command never answers a prompt, whatever the file says; the import warns.
8. A file with any command that answers is schema 5, as for *when connected*.
9. Only a saved connection gets a password. A local shell or a quick connect can send the defaults' commands by
   hand, but a typed-in address could be any machine, so any prompt is left to the user.
10. A chosen credential's password (root's) is never typed from the defaults for all connections, which reach every
    server: the editor won't save one there, and one in a hand-edited file isn't typed. On a folder the editor warns
    that it goes to every connection in it that asks, since a server that's been broken into could ask just to
    collect it. Each connection's own login password is its own, so that's allowed anywhere without a warning.
11. A command that asks first says which password it would type, naming the credential when the vault is unlocked.
12. The login password goes only to that login: not when the login credential is for another user than the session
    logged in as, nor at a prompt that names another user (`[sudo] password for root:`, `deploy@db's password:`).
13. A real password prompt hides what's typed. If the password is on screen afterwards and wasn't before, the prompt
    was something else (a program printing "Password:" and reading the line): the tab says so, that it's in the
    scrollback and any recording, and to change it, and nothing more is sent.
14. On connect, a command that types a password is sent only at a shell prompt. Others are still typed after 15
    seconds without one, but one typed at a prompt Orrery doesn't know (Telnet's `login:`) could hand its password
    to whatever reads it.
15. A vault unlock that a password prompt brings up says which connection and command it's for, and that cancelling
    leaves the prompt to the user.

Each outcome that leaves something undone or at risk (no prompt, a refused password, the wrong user, a password on
screen, the vault left locked, a command not sent) is said in a bar above the session that stays until dismissed,
pushing the session down rather than covering it, and read out. The status line's notices go after a few seconds and
show in the main window even for another tab, which is too easy to miss for these.

What the editor says, whenever a command will type a password (as warnings with an icon, read out as warnings):

- Always: it's typed once, only at a password prompt within 10 seconds, never at a shell prompt, and any program on
  the server can show such a prompt, so use it only on servers you trust.
- On a folder, with a chosen credential: every connection in it gets this password when it asks, and one server
  there that's been broken into could ask just to collect it. On the defaults that choice is refused, as an error.
  With the login password, a plain note that each connection types its own.
- Reaching a Telnet or serial connection: the password crosses the line as plain text, as the login does.
- Always, as a plain note: the safer ways (a NOPASSWD sudoers rule, or a key for root).

**Become root** in the editor fills in `sudo -i` (as `sudo su`, one process fewer), answered with the login password.
`su -` is the same with root's credential chosen. The connection pane marks such a command *types a password*.

Not built yet:

- A way to connect without the commands: Ctrl+click Connect, or *Connect without its commands* on the menu.
- A marker on the tab of a session that became root.
- Waiting for other text (`login:` on a serial console) and sending the username, for Telnet and serial logins.

SFTP has no shell, so files as root would be a feature of its own (starting `sftp-server` through sudo).

## 3. What we'd suggest instead

Settings should say that the safest root is set up on the server: a NOPASSWD sudoers rule for the commands needed, or
logging in as root with a key where that's allowed. Neither keeps a sudo password in Orrery. The password steps are
for servers where that can't be changed.
