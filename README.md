# Mode Switcher

<p align="center">
  <img src="assets/ModeSwitcher.png" alt="Mode Switcher icon" width="160">
</p>

A silent Windows utility that switches a PC between gaming and working setups.
There is no main window: launch the executable once to enter the opposite mode.

| Setting | Game mode | Work mode |
|---|---:|---:|
| HDR on every supported active display | On | Off |
| Windows **Do not disturb** | On | Off |
| Skydimo lighting on every detected device | On | Off |
| Steam | Open | No change |

After each switch, a custom bottom-right confirmation displays `Game mode on` or
`Work mode on`. It closes when clicked or fades automatically after 10 seconds.

## How it works

- The first launch enters **Game mode**.
- Later launches alternate using the last saved mode.
- State and logs are stored in `%LOCALAPPDATA%\ModeSwitcher`.
- Only one mode switch can execute at a time.
- The master Windows notifications setting remains enabled. Game mode selects the
  `Priority only` Do not disturb profile; Work mode selects the unrestricted profile.
- Skydimo is started automatically when installed but not already running.

## Requirements

- Windows 11 x64
- An HDR-capable display for HDR switching
- Skydimo 3.x for lighting control
- Steam for the Game-mode launch action

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

# Displays only the custom popup
.\dist\ModeSwitcher.exe --notification-test
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

## License

Mode Switcher is available under the [MIT License](LICENSE).
