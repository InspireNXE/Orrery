# Orrery design: host groups

Status: **Agreed**, 2026-10-31 (owner's answers in §9)
Scope: letting a connection hold other connections ("guests"), so a host and what runs on it show as one item.

---

## 1. Goals and non-goals

**Goals**
- A hypervisor and its VMs, a jump box and what's behind it, or an appliance and its interfaces show as one row that expands, instead of a folder plus a connection with the same name.
- The host row stays a connection: double-click connects to the host.
- Guests take the host's route and session policy (jump host, clipboard, reconnect), the way connections take their folder's. Not its login.
- Files without hosts stay readable by older Orrery builds.

**Non-goals**
- Hosts inside hosts. One level only; folders already do general grouping.
- Folders inside a host.
- Showing guests' live state on a collapsed host (see §9).

---

## 2. Model

A **host** is any connection with at least one guest. There's no "is a host" flag: adding a guest makes one, and removing the last guest turns it back into a plain connection.

```
ConnectionEntry
  + Guests: IReadOnlyList<ConnectionEntry>   // changed only through ConnectionTree; stored nested under the host as "guests", left out when empty
  + GuestsCollapsed: bool                    // the row's expand state; written only when true, so ordinary connections don't carry it
  + [JsonIgnore] GuestOf: ConnectionEntry?   // set for a guest, null otherwise
```

- A guest's **`Parent` is its host's folder**, not the host. `Parent` stays a `ConnectionFolder`, so `Ancestors()`, folder depth and every walk up to the folders work unchanged. `GuestOf` is the extra link.
- **`ConnectionFolder.Descendants()` also returns guests**, each right after its host. Search, Ctrl+K, the jump-host picker, `AllConnections()`, counts, re-import detection and export then pick them up with no changes of their own.
- **`Path` includes the host** for a guest ("Prod / hv-01"), so typing the host's name in search or Ctrl+K finds its guests, and export warnings and the CSV folder column show where a guest is.
- **Invariants**, enforced by `ConnectionTree`: a guest has no guests; a guest's `Parent` equals `GuestOf.Parent`; folders are never guests (the list is typed, so a file can't express one).

**Alternatives considered**
- *Any node can hold any node.* It makes `Parent` untyped and brings back every "what does dropping onto a connection mean" question at every level, for nesting nobody has asked for.
- *Guests stay flat in the folder with a `HostId` reference.* It's simpler to store, but moving the host would have to move each guest too, and a hand-edited file could point a guest at a host in another folder.

---

## 3. Inheritance

`SettingsResolver.Resolve` walks guest → host → folders, but only for the settings a host passes down. Each setting is either **host-level** or **own**:

| Setting | Guest takes the host's? | Why |
|---|---|---|
| Saved credential, Username, Domain | No (owner, 2026-10-31) | A guest's login is its own; could become an opt-in per host later |
| Jump host | Yes | If the host is reached through a bastion, its guests usually are too |
| Share clipboard, Reconnect if it drops | Yes | A policy for the whole machine |
| Protocol, Port | No | An RDP guest under an SSH host is normal |
| Desktop size, Enlarge, SSH agent, Older algorithms | No | Per protocol and per machine |

For an "own" setting, the walk skips the host and goes straight to the folders, as now. In the Properties panel, a guest's Inherit hint reads "From hv-01" when the value comes from the host.

The split goes in one place, `InheritableSettings.PassedToGuests` and `KeptByGuests`, with a test that every setting is in exactly one, so a new setting can't be left out silently (like the `FillFrom` test).

---

## 4. Connection list

**Rows**
- A host gets the tree's expand arrow, plus the guest count after its protocol badge (the same muted number folders show).
- Guests are indented under the host and look like ordinary connections.
- The arrow expands and collapses. Double-click or Enter connects to the host, as for any connection. The arrow and the name never compete, which is the main risk with a row that both expands and connects.
- `GuestsCollapsed` is saved like a folder's `Expanded`. While filtering, hosts are force-expanded and the state isn't saved.

**Filter**
- A guest that matches shows its host row with just that guest.
- A host that matches shows the host and all its guests, the same rule as a matching folder.

**Context menu** (on a connection)
- **Add guest**: a new connection under it, selected and ready to rename. Hidden for guests.
- **Move out of host** (on a guest): it becomes a plain connection right after the host, in the same folder.
- **New connection** with a host selected adds beside the host, not under it. Only Add guest adds guests, so nobody makes a host by accident.

**Drag and drop.** Dropping onto a connection currently puts the item before or after it, split at half the row's height. For a target that can take guests, the row is split in three:

| Drop position on a connection row | Result |
|---|---|
| Top quarter | Before it (as now) |
| Middle half | As its guest |
| Bottom quarter | After it (as now) |

The middle band is offered only when the dragged item is a connection without guests and the target isn't a guest. Otherwise the row keeps the half-and-half split. The row under the pointer is highlighted to show which drop it will get. Dragging a guest out to a folder, or before or after another row, makes it a plain connection there.

**Delete and duplicate**
- Deleting a host asks what happens to its guests:
  - **Delete them too**: "hv-01 and its 6 guests will be deleted," the same as deleting a folder.
  - **Keep them**: they become plain connections, placed in a folder chosen from a list: the host's own folder (selected first), any other existing folder, or the top level.
- Duplicating a host copies its guests, with new ids, as duplicating a folder copies its contents.

---

## 5. Properties panel

- The title line under a guest's name reads "Guest of hv-01 · Prod" instead of the folder path alone.
- A host's title line shows "6 guests".
- Inherit hints name the host when the value comes from it (§3). Settings guests don't take from the host keep naming the folder.

---

## 6. Storage

- `connections.json`: a host's `connection` object gets `"guests": [ … ]` (connection objects, without the `type` field since they can only be connections) and `"guestsCollapsed": true` when collapsed.
- **Schema version.** An older Orrery would load the file, ignore `guests`, and drop them on its next save. So the store writes `schemaVersion: 2` only when the tree has at least one host, and 1 otherwise. Older builds already refuse a newer schema with "written by a newer version of Orrery", which is safe. A tree without hosts stays readable by them.
- **Loading** checks the invariants: a guest with guests, or an empty entry in `guests`, is a damaged file. Report it like other load errors; don't repair it silently.
- Backups and Move data copy the file as it is, so they need no change.

---

## 7. Import and export

**Orrery export and import.** Guests are written nested, as in `connections.json`, and the export uses the same schema rule (2 only when a host is included). The importer remaps guests' ids with everything else and checks the same invariants. Exporting just a guest exports it as a plain connection, with the host-level values it had filled in.

**mRemoteNG export.** mRemoteNG has no hosts, so each host becomes a folder named after the host, holding the host connection first and then its guests. Every node already carries its own resolved values, so nothing is lost. A warning counts it: "Host groups were written as folders: 3."

**mRemoteNG import.** No change: confCons.xml has no hosts.

**CSV.** No new column: the Folder column shows the guest's path, which includes the host (§2).

---

## 8. Work and tests

Phases. Each one builds, passes the tests and is committed on its own.

1. **Core model and storage.** `ConnectionEntry.Guests`, `GuestOf` and `GuestsCollapsed`; `ConnectionTree.AddGuest`, `MoveToHost` and `MoveOutOfHost`; `Remove`, `Duplicate`, `Find` and `Descendants` covering guests; `Path`; the conditional schema version with invariant checks on load.
   Tests: round trip with and without hosts (schema 1 vs 2); invariant violations refused; delete and duplicate a host; `Descendants` order.
2. **Resolver.** The host step, the `HostLevel` set, and `Source` pointing at the host.
   Tests: each host-level setting inherited; each own setting skipping the host; a guest override; every setting classified.
3. **Connection list.** Rows, count, expand state, filter rules, Add guest, Move out of host, the three-band drop and its highlight, and the delete message.
   App tests in the style of `TabDragTests`/`PropertiesPanelTests` for drop placement and filtering.
4. **Properties panel.** Title lines and Inherit hints.
5. **Export and import.** Orrery nesting and schema, host-to-folder for mRemoteNG with its warning.
   Tests: Orrery round trip keeps hosts; mRemoteNG export imports back as folders with the right values.

**Touched:** `ConnectionNode.cs`, `ConnectionTree.cs`, `ConnectionStore.cs`, `SettingsResolver.cs`, `NodeViewModel.cs`, `MainWindow.axaml(.cs)` (menu, drop target, delete), `PropertiesPanel.cs`, `ConnectionExporter.cs`, `OrreryImporter.cs`.
**Should need no change, but needs a test each:** Ctrl+K search, the jump-host picker, recent connections (by id), and auto-reconnect (re-resolves on each connect).

---

## 9. Decisions (owner, 2026-10-31)

1. **Host-level settings.** Guests don't take the saved credential, username or domain. Jump host, clipboard and reconnect pass down as in §3. Desktop size and Enlarge stay per guest.
2. **Deleting a host.** Offer both: delete the guests, or keep them and choose where they go (the host's folder, another existing folder, or the top level). See §4.
3. **"Through its host" shortcut.** Yes, later: the jump-host picker offers "Through hv-01" for an SSH host's guests. Not part of phases 1 to 5.
4. **Collapsed host status.** Yes, later: a collapsed host shows when one of its guests has an open session.
