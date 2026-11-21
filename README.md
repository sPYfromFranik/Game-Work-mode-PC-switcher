# Mode Switcher

<p align="center">
  <img src="assets/ModeSwitcher.png" alt="Mode Switcher icon" width="160">
</p>

A small Windows utility that switches a PC between working, gaming, and music
setups. Launching it opens a system-themed chooser with one button per mode.

| Setting | Work mode | Game mode | Music mode |
|---|---:|---:|---:|
| HDR on every supported active display | Off | On | Unchanged |
| Windows **Do not disturb** | Off | On | Off |
| Skydimo device power | All off | All on | All on |
| Skydimo effect | — | Screen Mirror | Audio Sine |
| App action | No change | Open Steam | Open Spotify and play |

After each switch, a custom bottom-right confirmation displays `Work mode on`,
`Game mode on`, or `Music mode on`. It closes when clicked or fades automatically
after 10 seconds.

## How it works

- The chooser highlights the most recently selected mode.
- State and logs are stored in `%LOCALAPPDATA%\ModeSwitcher`.
- Only one mode switch can execute at a time.
- The master Windows notifications setting remains enabled. Game mode selects the
  `Priority only` Do not disturb profile; Work mode selects the unrestricted profile.
- Skydimo is started automatically when installed but not already running. Game
  and Music apply their effect to every detected device scope.
- Music mode avoids sending another Play command when Spotify's window title
  indicates that a track is already playing.

## Requirements

- Windows 11 x64
- An HDR-capable display for HDR switching
- Skydimo 3.x for lighting control
- Steam for the Game-mode launch action
- Spotify for the Music-mode launch and playback action

Missing optional software or unsupported hardware is recorded in the log rather
than shown in a dialog.

## Build

Install the .NET 8 SDK, then run:

```powershell
dotnet publish ModeSwitcher.csproj -c Release -o dist
```

The release build is self-contained and produces `dist\ModeSwitcher.exe`. Release
symbols are disabled, and local source paths are not included in the published app.

Useful diagnostics:

```powershell
# Checks integrations without changing the current mode
.\dist\ModeSwitcher.exe --dry-run

# Checks a specific mode plan without applying it
.\dist\ModeSwitcher.exe --dry-run --mode=Music

# Displays only the custom popup
.\dist\ModeSwitcher.exe --notification-test --mode=Music
```

## Local data

The application writes only these local files:

- `%LOCALAPPDATA%\ModeSwitcher\state.txt`
- `%LOCALAPPDATA%\ModeSwitcher\ModeSwitcher.log`

Set `MODESWITCHER_DATA_DIR` to redirect them during automated tests.

## Notes

HDR is controlled through Windows display-configuration APIs. Skydimo integration
uses its loopback-only JSON-RPC WebSocket API and does not send lighting data over
the internet.

The Work, Game, and Music glyphs are from Font Awesome Free 7.3.1; the original
SVG files and their attribution comments are retained in `assets`.

## License

Mode Switcher is available under the [MIT License](LICENSE).
