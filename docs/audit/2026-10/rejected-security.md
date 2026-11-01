# Rejected security claims, round 2026-10

Claims a verifier disproved, with the reason. Don't report these again unless the code they depend on changes.

| Claim | Where | Why it was rejected |
|---|---|---|
| VNC TLS, SecureTunnel and VeNCrypt TLS accept any certificate, so TLS gives no MITM protection | src/Orrery.Protocols.Vnc/VncSession.cs:84-118 | The accept-all is real, but a man in the middle can just as easily strip TLS from the offered security types and fall back to classic VNC, which is the accepted risk. The UI never claims a session is encrypted. The part that does matter, the full password leaking through these types, is S-2, and S-2's fix removes them. |
| A VNC server can pick security type None when a password is configured, with no warning | VncSession.cs:84-118, 491-501 | Classic VNC auth doesn't authenticate the server, so a rogue server accepts any password anyway (accepted risk). None sends less than VncAuth. |
| VNC server clipboard text length isn't capped | VncSession.cs:390-396 | The library drops ServerCutText above 262144 bytes before calling us (`ServerCutTextMessageType`). |
| VNC clipboard sharing is on by default and automatic in both directions | src/Orrery.App/Sessions/RemoteFrameView.cs, src/Orrery.Core/Connections/SettingsResolver.cs:38 | It matches the app's RDP default and TigerVNC, RealVNC and TightVNC, and there's a per-connection switch. The crash in the receive path is S-15. |
| RDP's NLA-only behavior rests on an unstated IronRDP default | src/Orrery.Protocols.Rdp/RdpSession.cs:505-520 | Probed: the default Connection Request asks for HYBRID and HYBRID_EX only (0x0A), and a server selecting SSL is rejected. The certificate is checked before credentials. Kept as a hardening note. |
| The first-use certificate prompt defaults to "Trust and connect" | src/Orrery.App/Security/CertificatePromptWindow.cs:81-88 | A deliberate choice, the same as the SSH prompt and mstsc. A changed certificate or key defaults to Cancel. |
| The SSH host key prompt sits inside the 20 s connect timeout, and a late Trust still pins | src/Orrery.Protocols.Ssh/SshSession.cs:61-74 | A usability bug. The pinned key is exactly the one the user saw and chose, `Connect` has already failed so no credentials follow, and Reconnect validates again. |
| An unterminated DCS/SOS/PM/APC string swallows `ESC c`, so `reset` can't recover | src/Orrery.Core/Terminal/TerminalEmulator.cs:299-306 | `tput reset`'s `rs1` for xterm-256color contains a BEL, which ends the string, and CAN, SUB and the usual prompt's BEL end it too. It's a parser correctness bug, and a hostile host can hide output in simpler ways. |
| `--moved-from <path>` is trusted to name a folder to delete | src/Orrery.App/App.axaml.cs:84-85 | It only arrives on the command line. There's no URL protocol or file association, and planting a shortcut needs the same user's access (accepted risk). The hardening is folded into S-3's fix direction. |
