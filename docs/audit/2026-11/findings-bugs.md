# Bug pass, round 2026-11

Run 2026-11-03 on `dev` at `058f90d`, after the security pass, at the owner's request ("do another round of bug
testing. Verify nothing is found and if it is, fix it"). Five finders, each on one area, reading only. Each area's
fixes were then made in their own worktree, with a failing test first where one could be written, and a sixth reader
went over all five fix commits for anything they broke. It found four, fixed in the last commit (B2-32 to B2-35).

The finders found 28 and the reader over the fixes 4 more: 32 in all, all fixed, none open. One (B2-20) was a regression from S-4, built earlier the same day.

## RDP (`293bf14`)

- B2-1: keys and clicks were each sent from their own task, so they could reach the server out of order: swapped
  letters, a modifier or button left down. They now go out in the order they're applied. Test: `RdpInputOrderTests`.
- B2-2: a Kerberos reply was read with one 8 KB read, so a long or split reply failed CredSSP and looked like a
  refused login. It's read by its length prefix, up to 1 MB. Test: `RdpConnectorTests` (three cases).
- B2-3: the session wrapped the connector's stream again, dropping bytes the server had already sent. The connector's
  own is kept.
- B2-4: only the first DNS address was tried, and none at all threw. Each is tried in turn; none says the name wasn't
  found. Test: `RdpConnectorTests` (three cases).
- B2-5: closing a tab on a dead connection waited for TCP to give up. The shutdown waits 2 seconds, then closes the
  connection.
- B2-6: a resize asked for before the server opened its display channel was dropped. It's sent once the channel is
  ready.

## SSH, Telnet, terminal (`6b08882`)

- B2-20: S-4 made a jump host's forward take one connection, but the retry after a slow host-key answer dials it
  again. That retry now lets exactly one more in. Test: `SshTunnelTests.A_retry_after_a_slow_host_key_answer_gets_exactly_one_more_connection`.
- B2-21: Telnet through a jump host showed Connected, then a clean Disconnected, when the far side couldn't be
  reached. It fails, saying so. Test: `TelnetRouteTests`.
- B2-22: CSI sequences with `<` or `=` (the kitty keyboard protocol, DA3) ran as plain CSI: `ESC[<u` moved the
  cursor. They're ignored. Test: `TerminalEmulatorTests.Ignores_csi_with_a_less_than_or_equals_prefix`.
- B2-23: a resize while the SSH shell or local terminal was opening was lost. It's sent once the shell is open.
- B2-24: disconnecting an SSH or SFTP session while it connected could mark it Failed. It ends Disconnected.

## Recording (`ed8bc09`)

- B2-10: a screen recording with nothing saved in it broke the player. It says there's nothing to play. Test:
  `RecordingUiTests.A_screen_recording_with_nothing_in_it_says_so_rather_than_playing`.
- B2-11: a canceled AVI export left the half-written, unencrypted file on Windows. Test:
  `RecordingUiTests.A_canceled_avi_export_leaves_no_file_behind`.
- B2-12: the player's Export used the player's key, which locking the vault zeroes. It gets its own copy.
- B2-13: Skip idle and Shift+arrows while a recording loaded could throw. They wait for the load.
- B2-14: an ffmpeg export hung for good if reading the recording failed. ffmpeg's input is always closed, and Cancel
  stops it.
- B2-15: the recordings list's description cache was read across threads without a lock.

## Core (`96a34f7`)

- B2-40: duplicating a folder left its hosts' guests with no folder, so they lost its settings. Test:
  `HostGroupTests.Duplicating_a_folder_puts_its_hosts_guests_in_the_copy_so_they_take_its_settings`.
- B2-41: exporting a host on its own dropped what its guests took from its folders. Test: `ConnectionExporterTests`.
- B2-42: "skip already imported" dropped new guests of a host already there. The host comes in again holding only its
  new guests. Test: `ConnectionExporterTests`.
- B2-43: the same skip didn't re-point the file's top-level jump host. Test: `ConnectionExporterTests`.
- B2-44: importing a file's contents without a new folder lost its defaults for guests of top-level hosts. Test:
  `HostGroupTests`.
- B2-45: Move data copied only mRemoteNG import reports. Every kind is covered. Test: `DataMoverTests`.
- B2-46: an mRemoteNG web entry with `https://` in its host couldn't open. The address decides. Test:
  `MRemoteNgImporterTests.A_web_page_whose_host_is_a_full_address_opens_that_address`.
- B2-47: a number Orrery doesn't know for a choice (a protocol of 99, say) got past import and threw later. Imports
  refuse it; `connections.json` falls back to inherited. Tests: `ConnectionExporterTests`, `ConnectionModelTests`.

## Main window (`4bbdf07`, then the follow-up commit)

- B2-30: reconnecting or duplicating a deleted connection's tab went without its folder's jump host and login. A
  connection being deleted keeps what it inherited first. Test: `UnsavedEditsTests.A_deleted_connection_keeps_what_its_folders_and_host_gave_it`.
- B2-31: properties that didn't validate were thrown away silently on a selection change, a vault lock, and several
  other actions. They stay, with the error, and the action waits. Tests: `UnsavedEditsTests`, `PropertiesPanelTests`.
- B2-36: on layouts where AltGr+B types `{`, Ctrl+Alt+B toggled the properties pane and ate the character. It goes to
  the session. Test: `AltGrShortcutTests`.

## Found in the fixes

- B2-32: the SSH resize catch-up could send an older size after a newer one. Both are sent under the lock.
- B2-33: B2-21 failed any quiet Telnet service that closed an idle connection. Only an end within 5 seconds counts.
- B2-34: with bad edits, right-clicking another row ran the menu on the edited one. The menu stays shut and the error
  shows.
- B2-35: with bad edits, a filter hiding their row left nothing selectable. The filter is cleared. Test:
  `UnsavedEditsTests.A_filter_that_hides_a_row_with_bad_edits_is_cleared_so_the_row_can_be_fixed`.

## Not checked on a real machine

RDP input order, shutdown, Kerberos and early resize (B2-1 to B2-6) need a live server; the AltGr check needs a
Czech layout; the ffmpeg export needs ffmpeg.
