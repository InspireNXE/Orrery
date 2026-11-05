# Screenshots

The images in `docs/images` are drawn by `tools/Orrery.Screenshots`. To make them again, after running
`./build/Get-IronRdp.ps1` once as for any build:

```powershell
dotnet run --project tools/Orrery.Screenshots
```

It overwrites the PNGs in `docs/images`. Pass a folder to write them somewhere else instead:
`dotnet run --project tools/Orrery.Screenshots -- <folder>`. It takes a few seconds once built, and draws the same
pixels each time on the same machine.

## How they're made

The tool runs Orrery's own `App`, `MainWindow`, `SettingsView` and `QuickConnectWindow` on Avalonia's headless
platform with real Skia rendering, so the pictures show the app's actual layout, styles and fonts.

- The data is made up: a Production and a Lab folder with hosts under `.example`, and the username `demo`. The tool
  writes it to a new folder under the system temp folder and deletes that folder when it's done.
- It runs as a portable copy, so credentials go to a vault file in that temp folder. It never opens the system
  keychain, your Orrery data folders, or Windows Hello.
- The sessions are fakes. The SSH tab shows text the tool writes into the terminal, and nothing connects anywhere.
  The recording shots record that fake session into the temp folder and play it back.
- The real host check is turned off. A fake one says which demo hosts are up or down, so no host is probed.
- The status bar's data folder line is replaced with a made-up path.

The tool is in `Orrery.sln`, so it keeps building, but it isn't a test project and the test run doesn't start it.

## What they can't show

- The window's minimize, maximize and close buttons belong to the operating system, which the headless platform
  doesn't draw, so the right end of the title bar is empty.
- Quick connect is a window of its own. The tool draws it over the dimmed main window where Orrery places it.
- The images are at 1x scale (1280×800 for the window).
- The terminal uses the first of Cascadia Mono, Consolas and Menlo that's installed, so images made on Linux or
  macOS look slightly different. The checked-in ones were made on Windows.

## The images

| File | Shows |
| --- | --- |
| `main-window.png` | The main window: the connection tree with session and host marks, an SSH tab, and the properties pane |
| `connection-lost.png` | The connection-lost screen for a login the server refused |
| `quick-connect.png` | Quick connect with "web" typed in |
| `side-by-side.png` | web-01 and web-02 side by side, web-01 finding "nginx" in its scrollback |
| `saved-commands.png` | Quick connect with ">saved" typed in: the saved commands web-01 has from Production, the defaults and its own |
| `commands-page.png` | The Commands page, with a command saved on web-01 and build-agent open in the right-hand pane |
| `settings-appearance.png` | Settings › Appearance with a color vision chosen |
| `recording.png` | An SSH tab being recorded, with the red dot and the REC pill |
| `recording-playback.png` | The Recordings panel and player, paused partway through that recording |
| `interface-colors.png` | The connection tree in Standard and Clearer interface colors |
| `social-preview.png` | GitHub's social preview, 1280×640. Upload it under the repository's Settings › General › Social preview |

Look at every image before committing it.
