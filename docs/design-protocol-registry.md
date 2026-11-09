# Orrery design: protocol registry

Status: **Implemented**, 2026-11-08, on `claude/exciting-poincare-c9b8a0` (phases 0–6, §6); not merged.

As built, two names differ from the sketch below: the Core table is `ProtocolTraits` (`ProtocolTraits.Of`, `.All`), since a
Core class named `Protocols` hid the App namespace `Orrery.App.Protocols`; and the App side is an abstract class,
`ProtocolDescriptor`, listed in `ProtocolRegistry`, rather than an `IProtocolDescriptor` interface.

Starting a session (§4.3) as built: a descriptor's `StartAsync(ISessionHost, StartRequest)` starts it once the connect
steps are done. `StartRequest` carries a `SessionLogin` (password, saved credential, key file, or SSH keys only with a
retry) and `StartOptions` (a saved connection's start settings, or quick connect's own defaults). A local shell and a
web page don't start that way: their `OpenAsync` hands them to the window's own code for them, which the toolbar and
management pages also use. The connect steps are guarded by `tests/Orrery.App.Tests/ProtocolGuardConnectTests.cs`.

What phase 6 kept on purpose: `ProtocolInfo` stays as a few short names over the traits. The checks still naming a
protocol are about that protocol itself: the importers and the exporter's list of what doesn't carry over, web
addresses, quick connect's guesses from what's typed, VNC's view-only switch in This session, and jump hosts, which
are SSH connections. A local shell's tab still says `Protocol.Ssh` with `Local` set, because the workspace file keeps a
tab's protocol and Orrery 0.1.1 has no `Local`: it would drop the whole workspace. A network protocol that can't use
jump hosts (OSP) still needs a connect step that refuses one with its reason; web pages refuse in their own code.

Scope: one place that describes each protocol, so adding a protocol means writing a descriptor instead of editing
about fifteen switches and type checks. Internal only: the descriptors are compiled into Orrery.

Line numbers below are from `dev` at `7568f2d`.

---

## 1. Goals and non-goals

**Goals**
- Adding a protocol (OSP first) means one enum value, one descriptor class, its properties section and its
  session code. MainWindow, `PropertiesPanel`, quick connect, the exporters and host status don't change.
- The seven protocols behave exactly as they do now. `connections.json`, the recent list, workspaces and recordings
  are written byte for byte as before.
- A slim app built on OSP can list just the descriptors it needs.

**Non-goals**
- Loading third-party plugins: no `AssemblyLoadContext`, no public or versioned plugin API, no plugin signing. Code
  loaded that way would run in-process next to the credential vault, and it doesn't fit single-file publishing.
  Nothing here rules it out later, but nothing here builds toward it either.
- Changing how settings are stored. `InheritableSettings` stays one flat bag (§5).
- Rewriting the connect pipeline. Logging in, jump hosts and prompts stay in MainWindow; they move behind an
  interface the descriptors call (§4.3).

---

## 2. What's hand-wired today

| Place | What it switches on |
|---|---|
| `Protocol` enum, `ProtocolInfo` (`Core/Connections/ConnectionNode.cs:6`, `:30`) | Default port, label, `IsWeb`, `UsesNetwork`, `IsDesktop`, `AddedInSchema3/6` |
| `MainWindow.ConnectAsync` (`:2449`) | Local first, the "no host" message, web, SSH files, serial, jump hosts, Telnet's missing login, SSH keys and agent |
| `MainWindow.ConnectDirectAsync` (`:2635`), `StartSessionAsync` (`:2681`) | Web, serial, Telnet, SSH agent; then SSH, VNC, else RDP |
| `ChecksHostDirectly` (`:3400`), `Encrypted` (`:3715`), `DescribeSession` (`:3687`) | Which protocols are probed over TCP, which are encrypted, what the status line says |
| About ten `is RemoteFrameView` checks (MainWindow `:1508`, `:1607`, `:3342`, `:3705`, `:3806`, `:3893`, `:3999`; `PropertiesPanel:577`, `:590`; `RecordingService:66`, `:125`, `:201`) | Full screen on chosen screens, the Windows key hook, view only, screenshots, popout, color correction, pointer shapes in recordings |
| `ProtocolStyle` (`App/Connections/ProtocolStyle.cs`) | Badge colors and icon |
| `QuickConnectWindow.Schemes` (`:32`), `Candidates` (`:107`), `DirectVerb` (`:456`) | URI schemes, which protocols get a second guess, "Open" vs "RDP to" |
| `PropertiesPanel.ShowSections` (`:1096`), its constructor, `Load`, `TrySave`, live rows (`:1723`) | Which sections and rows show; reading, checking and saving each protocol's settings |
| `SessionSettingsView.FixedSettings`, `ProtocolName` (`:275`, `:303`) | What a running session can't change; protocol names |
| `ConnectionExporter` (`:369-436`, `:400`), `MRemoteNgImporter` (`:173`, `:629-743`) | mRemoteNG protocol names, settings lost on export, per-computer hosts |
| `HostStatus.ProbeTarget.For` (`Core/Network/HostStatus.cs:39`) | Which protocols can be checked and why not |
| `ConnectionStore` log masking (`:134`), `PerComputerFields` (`:28`), `SavedCommand` (`:197`), `SettingsResolver` (`:198`), `SetupChoices` (`:147`) | Whether the host is a network name, whether it's per computer, terminals, jump hosts being SSH |

---

## 3. Shape

Two layers, because Orrery.Core can't see Avalonia or the protocol projects, and the importers, exporters,
host status and settings resolver need to know about protocols too.

```
Orrery.Core/Connections/Protocols.cs          ProtocolTraits records + Protocols.Get(protocol), no UI
Orrery.App/Protocols/IProtocolDescriptor.cs   Traits + style + session factory + properties section
Orrery.App/Protocols/RdpProtocol.cs           one per protocol (Rdp, Vnc, Ssh, Telnet, Serial, Web, Local)
Orrery.App/Protocols/ProtocolRegistry.cs      the list, looked up by Protocol
```

### 3.1 Identity stays the `Protocol` enum

The enum is what's stored: `"rdp"`, `"ssh"` and so on, camelCase through `JsonStringEnumConverter`, in
`connections.json`, `recent.json`, workspaces, recordings and the history. Keeping it means nothing on disk changes
and `Resolved<Protocol>`, `SessionOrigin`, `RecentConnection` and the rest keep their types.

Adding a protocol still adds an enum value, and that's deliberate: an older build that reads a value it doesn't know
throws, so a new protocol always comes with a schema bump (§5), and the enum line is where that's remembered.

*Considered:* string ids (`"osp"`). The JSON would be the same, but every `Protocol`-typed field and record would
change for no gain while all protocols are compiled in. Worth revisiting only if plugins ever happen.

### 3.2 Core: `ProtocolTraits`

```csharp
public sealed record ProtocolTraits(
    Protocol Protocol,
    string Label,                 // the badge: "RDP", "TELNET"
    string Name,                  // in sentences: "RDP", "Telnet", "serial port"
    int DefaultPort,              // 0: none, never shown
    IReadOnlyList<string> Schemes,// "rdp", "ssh"; quick connect and import read them
    SessionKind Kind,             // Desktop, Terminal, Web
    Reach Reach,                  // Network, ThisComputer (serial port, local shell)
    JumpHosts JumpHosts,          // Native (SSH), Tunnel (RDP, VNC, Telnet, web), None (serial, local, OSP: UDP)
    HostProbe HostProbe,          // Tcp, None. OSP would add Quic or stay None
    Encryption Encryption,        // Always, Never, ByScheme (web), NotApplicable
    LoginKind Login,              // None (serial, Telnet, local, web), Password (RDP, VNC), SshKeys (SSH)
    int AddedInSchema,            // 1 for RDP/SSH/VNC/Telnet, 3 serial and web, 6 local
    HostField Host,               // the host box's label and placeholder, and whether it has a chooser
    IReadOnlyList<string> MRemoteNgNames); // "SSH2", "SSH1"; the first is what's exported. Empty: not exported

public static class Protocols
{
    public static ProtocolTraits Get(Protocol protocol);
    public static IReadOnlyList<ProtocolTraits> All { get; }
}
```

`ProtocolInfo` stays as a thin front so its twenty callers don't all change in one commit: `DefaultPort(p)` becomes
`Protocols.Get(p).DefaultPort`, `UsesNetwork(p)` becomes `Reach == Network`, and so on. It can be inlined later or
left.

Http and Https stay two enum values (they're stored that way) with two traits records that share one App descriptor.

### 3.3 App: `IProtocolDescriptor`

```csharp
interface IProtocolDescriptor
{
    IReadOnlyList<ProtocolTraits> Traits { get; }       // one, or two for web

    ProtocolStyle Style { get; }                        // background, foreground, icon geometry

    /// <summary>Opens a tab, a popout or the browser for one resolved connection.</summary>
    Task StartAsync(StartRequest request, ISessionHost host);

    /// <summary>The protocol's own properties section, or null. Shown for its connections, and for folders and the defaults.</summary>
    IPropertiesSection? CreateSection(PropertiesContext context);

    /// <summary>What a running session of this protocol can't take up until it reconnects.</summary>
    IEnumerable<(string Label, string Value)> FixedSettings(ResolvedSettings saved, bool enlargeByDefault) => [];
}
```

`ProtocolRegistry` is a static list built once in `App`, in the order the protocol picker shows them (Local first, as
today). It checks at startup, in debug builds and in a test, that every enum value has exactly one descriptor.

---

## 4. The pieces

### 4.1 Style, badges and quick connect

`ProtocolStyle.Background/Foreground/Icon(p)` read the descriptor. The seven color pairs and icons move into their
descriptors unchanged.

Quick connect builds `Schemes` from every protocol's `Traits.Schemes`. `Candidates` keeps its rule (a typed scheme,
a serial port or a web address is one choice; otherwise the likely one of RDP and SSH and then the other), but asks
"is this a network protocol with a login" instead of naming Serial, Http and Https. `DirectVerb` uses `Kind == Web`.

### 4.2 Capabilities instead of type checks

The `is RemoteFrameView` checks become checks for a small interface in Orrery.App that `RemoteFrameView` implements,
and OSP's view would too:

```csharp
interface IDesktopView
{
    bool ViewOnly { get; set; }
    DesktopScaling Scaling { get; set; }
    bool AllowsColorCorrection { get; }
    bool HasPointerShapes { get; }                      // recordings keep the pointer
    IReadOnlyList<Screen> ScreensFor(FullScreenChoice?); // full screen on chosen displays
    Task<Bitmap?> CaptureAsync();                      // screenshots
}
```

Only what those ten places use goes in; anything RDP-only (the Windows key hook's scancodes, `NewRemoteFrameView`'s
options) stays on `RemoteFrameView`. Session-side capabilities already work this way (`IFrameBufferSession`,
`ITerminalSession`, `IAudioSession`) and don't change.

`Encrypted(tab)` reads `Traits.Encryption` (web keeps asking the page's address). `DescribeSession` keeps its view
cases and gets "View only" from `IDesktopView`. `ChecksHostDirectly` becomes `HostProbe == Tcp`.

### 4.3 Starting a session

`ConnectAsync` and `ConnectDirectAsync` keep the steps every protocol shares, driven by traits instead of names:

1. Save pending property edits; resolve the settings.
2. No host and `Reach == Network` (or a serial port): say so, using `Traits.Name`.
3. `JumpHosts == Tunnel`: build the tunnel with `JumpHopsAsync`. `None` with a jump host set: refuse with the
   protocol's reason (OSP: "UDP can't go through SSH tunnels…"). `Native`: hand the hops to the protocol.
4. `Login == Password` or `SshKeys`: read the saved credential, the key file and the agent, and prompt if needed,
   exactly as now. `None`: skip.
5. Call `descriptor.StartAsync(request, host)`.

```csharp
sealed record StartRequest(
    SessionOrigin Origin,
    ConnectionEntry? Entry,        // null for quick connect
    ResolvedSettings Settings,     // the connection's, or the defaults for quick connect
    ConnectionTarget Target,
    Login Login,                   // password, saved credential, key file, agent; Login.None
    SshRoute Route);               // jump hops, forwards, keep-alive

interface ISessionHost               // MainWindow implements it
{
    void Open(SessionOrigin origin, Func<(Control View, IConnectionSession Session)> start);
    ISessionRoute? Tunnel(IReadOnlyList<SshHop>? jumps);
    AppSettings Settings { get; }
    RemoteFrameView NewDesktopView(SessionOrigin origin, IFrameBufferSession session, bool enlarge, bool followsViewSize);
    Task OpenWebPageAsync(...);    // web and the management page
    Window Owner { get; }          // for a protocol's own prompts (SSH's host key, OSP's pairing PIN)
}
```

`StartVnc`, `StartRdp`, `StartTelnet`, `StartSerial`, `StartSshAsync`/`StartSshWithKeys`, `OpenLocalTerminal` and the
web path move, mostly as they are, into their descriptors. SSH's key-refused retry stays: the request carries a
`Retry` callback that reconnects with keys skipped.

The local shell and web paths start before step 2 today; their descriptors say `Login.None` and `Reach`, and the
"no host" rule skips a local shell (its empty host is the shell chosen in Settings).

What the descriptors don't touch: the vault, the prompt window, credential logging, the history, reconnecting, the
tab strip, recording. They stay in MainWindow and the services it already has.

**Popouts (for OSP).** OSP wants a session that can own its window (design-stream §8). That isn't needed to start
the registry. When popout lands, `ISessionHost.Open` grows an option, or `StartAsync` calls a second `ISessionHost`
method, so the descriptor chooses tab or popout. Noted here so the interface isn't frozen around tabs.

### 4.4 Properties pane

What's shared stays in `PropertiesPanel` and shows by traits:

| Section or row | Shows when |
|---|---|
| Port, host check, Wake-on-LAN, management page | `Reach == Network` |
| Credentials | `Login != None` |
| Domain | `Login == Password` and the protocol uses domains (a `HasDomain` trait: RDP) |
| Clipboard, display (scaling, quality, enlarge, full screen), keyboard shortcuts | `Kind == Desktop` (keyboard: on Windows) |
| Record typing, saved commands | `Kind == Terminal` |
| Jump host | `JumpHosts != None` |
| Host box label, placeholder, chooser button | `Traits.Host` |

A folder or the defaults still show every section, since what they set reaches connections of every protocol.
A host still shows what it passes to its guests.

Each protocol's own section moves out of `PropertiesPanel` into a class:

```csharp
interface IPropertiesSection
{
    Control View { get; }
    void Load(ConnectionNode node, InheritableSettings own, ResolvedSettings inherited);
    string? Validate();                                   // the message to show, or null
    void Save(InheritableSettings into);
    IEnumerable<(InheritRow Row, string Key)> LiveRows { get; } // rows a running session takes up now
}
```

`RdpSection` (desktop size, audio, drives, console), `VncSection` (view only), `SshSection` (key file, keep-alive,
SOCKS, legacy algorithms, forwards), `SerialSection` (line settings) and `LocalSection` (start folder, administrator).
`InheritRow`, `Switch()` and the helpers they use move out of `PropertiesPanel` to `App/Connections/Properties/` so
sections can use them. `PropertiesContext` gives a section the dirty-marking callback, the node, the tree and the
owner window.

This is the biggest single move (several hundred of `PropertiesPanel`'s 1,984 lines) and the one most likely to
conflict, so it's its own phase (§6, phase 4).

`SessionSettingsView` gets each protocol's fixed settings from `FixedSettings`, and its `ProtocolName` from
`Traits.Name`.

### 4.5 Import, export and host status

- `MRemoteNgImporter`'s name table is built from `MRemoteNgNames`; export writes the first. A protocol with none is
  left out of an mRemoteNG export with a warning, as serial and local are now.
- The "lost on export" warnings stay in `ConnectionExporter`: they're about what mRemoteNG can hold, not about the
  protocol. Its per-computer test (`:400`) and `PerComputerFields` use `Reach == ThisComputer`.
- `ProbeTarget.For` says why not from `Reach` and `HostProbe`; `ConnectionStore`'s log masking learns the host as a
  host name only when `Reach == Network`.
- `SettingsResolver`'s "a jump host must be SSH" check reads `JumpHosts == Native`, so a jump host is still only
  ever SSH today.

---

## 5. Settings and schema

**Storage doesn't change.** `InheritableSettings` stays flat and protocol-agnostic: RDP's `DesktopSize` and SSH's
`KeyFile` sit side by side, null when unset, inherited field by field. Splitting it into per-protocol objects would
change the JSON and the inheritance rules, and older builds would drop the moved settings on save.

**Schema rules move next to the protocol, not into it.**
- `ProtocolInfo.AddedInSchema3/6` become `Traits.AddedInSchema`. `InheritableSettings.NeedsSchemaN` asks
  `Protocols.Get(p).AddedInSchema >= N` instead of naming protocols. `SchemaFor`, the `SchemaVersion*` constants and
  the `AddedInSchemaN` property sets stay where they are.
- A new protocol bumps `SchemaVersion`, sets its `AddedInSchema` to it, and adds its settings to a new
  `AddedInSchemaN` set. That's the same work as today, with the protocol half of it declared in one place.
- The camelCase enum converter, `BritishSpellingEnumConverter` and every file's options stay as they are.

**Guard tests**, written before anything moves (phase 0):
- Every `Protocol` value has traits and a descriptor; enum names round-trip through each file's options as the same
  camelCase strings.
- `SchemaFor` gives today's answer for a tree of each protocol, alone and mixed.
- A golden `connections.json`, recent list and workspace with one connection of each protocol are written byte for
  byte the same before and after.
- Every trait the seven get is pinned to today's behavior (port, label, `UsesNetwork`, `IsDesktop`, probing,
  encryption, mRemoteNG names), so a typo in a descriptor fails a test rather than a user.

**Open for OSP:** its fifteen or so settings (§9 of design-stream) could go flat into `InheritableSettings` with a
`Stream` prefix (consistent, inherits per field), or into one nested `stream` object (tidier, but a new inheritance
path). Flat is recommended. It's OSP's call, not the registry's.

---

## 6. Migration plan

Each phase is one or a few commits that build, pass the tests and leave Orrery working. No phase changes behavior.

| Phase | What | Touches MainWindow? |
|---|---|---|
| 0 | Guard tests (§5) on today's code | No |
| 1 | Core `ProtocolTraits` and `Protocols`; `ProtocolInfo` delegates; importer, exporter, host status, store masking, `PerComputerFields`, `SavedCommand`, `SettingsResolver` read traits | No |
| 2 | App descriptors with style only; `ProtocolStyle`, quick connect, `RecentPanel`, `SessionTab`, `NodeViewModel`, `SessionSettingsView` read them | Barely: `DirectVerb`-style label lines |
| 3 | `IDesktopView`; the ten `is RemoteFrameView` checks | Yes, about seven one-line changes |
| 4 | `InheritRow` and helpers out of `PropertiesPanel`; five protocol sections; `ShowSections` by traits | No (PropertiesPanel only) |
| 5 | `ISessionHost`, `StartRequest`; the `Start*` methods into descriptors; `ConnectAsync`/`ConnectDirectAsync` by traits; `ChecksHostDirectly`, `Encrypted` | Yes: several hundred lines move out |
| 6 | Tidy: inline `ProtocolInfo` where it reads better, remove dead switches, update `docs/roadmap.md` | Little |

The seven protocols, by descriptor:

| Descriptor | Kind / reach | Login | Jump hosts | Probe | Section | Notes |
|---|---|---|---|---|---|---|
| `RdpProtocol` | Desktop / network | Password + domain | Tunnel | TCP | `RdpSection` | Fixed settings: size, audio, drives, console |
| `VncProtocol` | Desktop / network | Password | Tunnel | TCP | `VncSection` | Starts view only if set |
| `SshProtocol` | Terminal / network | SSH keys | Native | TCP | `SshSection` | Files (SFTP) is a start option; key-refused retry |
| `TelnetProtocol` | Terminal / network | None | Tunnel | TCP | none | |
| `SerialProtocol` | Terminal / this computer | None | None | None | `SerialSection` | Host box is the port, with "Ports…" |
| `WebProtocol` (Http, Https) | Web / network | None | Tunnel | TCP | none | Encryption by scheme; opens a page, not always a tab |
| `LocalProtocol` | Terminal / this computer | None | None | None | `LocalSection` | Empty host is the shell chosen in Settings; elevation |

**OSP after the registry:** an `Osp` enum value, `OspProtocol` (Desktop / network, its own login via pairing,
`JumpHosts.None` with its reason, `HostProbe.None` until a QUIC probe exists, `AddedInSchema` 7), `OspSection`, and
its view implementing `IDesktopView`. No edits to MainWindow or `PropertiesPanel` beyond the popout option in §4.3.

---

## 7. Risks

- **Merge conflicts.** Phases 3 to 5 rewrite code that OSP's integration and work on `dev` also edit (§9).
- **Subtle behavior drift** in `ConnectAsync`, whose order of checks matters (local before the host check, web
  before jump hosts, Telnet before credentials). Phase 5 keeps the order and moves bodies verbatim; the guard tests
  can't see this, so it needs a manual pass over each protocol, including quick connect and reconnect.
- **The properties pane** loads, validates and saves in three separate places today. Getting a section's `Validate`
  order wrong changes which error shows first. Low harm, easy to see.

---

## 8. Alternatives considered

- **Leave it hand-wired and add OSP the same way.** Works, and is what OSP is doing for now. Each new protocol keeps
  costing fifteen edits, and a slim OSP app would have to carry or fork MainWindow's switches.
- **One descriptor per protocol with no Core layer.** Simpler, but the importers, exporters, host status and the
  resolver live in Core and would keep their own switches.
- **Loadable plugins.** Out of scope (§1).

---

## 9. Questions for the owner

1. **Approve the shape?** Enum kept as identity, Core traits plus App descriptors, flat settings, phases as in §6.
2. **When to start, and on what base?** This branch is at `main` (0.1.1); `dev` is 137 commits ahead and is where
   this would land, so the branch would be rebased onto `dev` first. Phases 0 to 2 and 4 barely touch MainWindow and
   could start any time. Phases 3 and 5 rewrite `ConnectAsync`, the `Start*` methods and the `RemoteFrameView` checks,
   which OSP's integration and the current `dev` work will also edit. Suggested: phases 0 to 4 now, and phase 5 at a
   quiet point **before** OSP's App integration starts, so OSP is written as a descriptor instead of being wired by
   hand and then moved.
3. **OSP's settings:** flat with a prefix (recommended), or a nested object (§5)?
