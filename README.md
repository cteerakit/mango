# Mango — Dota 2 Companion App

Windows desktop companion for Dota 2. Mango connects through Valve Game State Integration (GSI) and OpenDota to show a live match roster, player profiles, notes, and an optional in-game overlay.

## Features

**Companion app**

- **Match** — live Radiant/Dire roster with hero, rank, and match counts; OpenDota enrichment and local Steam roster discovery
- **Player** — overall stats and recent matches for saved players
- **Notes & votes** — per-player notes and like/dislike on the match scoreboard; import/export backups
- **Status** — GSI connection health and Dota setup checklist

**Overlay** (optional in-game HUD)

- GPM/XPM strip
- Objective timers: bounty, power, wisdom, and lotus pool runes
- Click-through overlay with interactive layout mode (drag widgets)
- Experimental build suggestions (Developer options)

## Requirements

- Windows 10 19041+ / Windows 11
- .NET 10 SDK (or .NET 8+ with Windows App SDK workload)
- Dota 2 installed via Steam
- Dota running in **Borderless Windowed** (not exclusive fullscreen)
- Steam launch option: `-gamestateintegration`

## Quick start

1. Open `Mango.sln` in Visual Studio 2022 or run:

```powershell
dotnet build Mango.sln
dotnet run --project src/Mango/Mango.csproj
```

2. On first launch, Mango installs `gamestate_integration_mango.cfg` into your Dota `cfg/gamestate_integration/` folder.
3. Add `-gamestateintegration` to Dota launch options in Steam if not already set.
4. Start a match — live data updates from GSI on `http://127.0.0.1:40000/`. Open **Match** in the companion window for the roster, or enable the overlay for in-game widgets.

## Reload during development

For in-place XAML and C# Hot Reload, open `Mango.sln` in Visual Studio, select the **Mango (Unpackaged)** launch profile and **Debug / x64**, then press **F5**. Save supported edits while the debugger is attached. Some edits require restarting the app. See [Microsoft's WinUI runtime design tools guide](https://learn.microsoft.com/en-us/windows/apps/develop/ui/xaml-runtime-design-tools).

For command-line development, close any running Mango instance, then run:

```powershell
.\scripts\watch.ps1
```

This watches source files, rebuilds, and restarts Mango after changes. It does not preserve the running UI state. Press **Ctrl+C** to stop.

### Work with mock data

In Mango, open **About > Open developer options** and turn on **Show mock data**. The same app shows sample match, player, and overlay data without a Dota match. The setting is saved across restarts, including when using `scripts\watch.ps1`.

Turn it off to return to live data. The GSI listener and hotkeys stay available, and the overlay remains controlled by its normal visibility toggle.

## Hotkeys

| Hotkey | Action |
|--------|--------|
| `Alt+F9` | Toggle overlay visibility |
| `Alt+`` | Toggle interactive mode (drag panels) |

## Settings

Stored in `%LOCALAPPDATA%\Mango\settings.json`:

- Overlay panel visibility, opacity, and layout positions
- Turbo mode timer rules
- GSI auth token
- Optional STRATZ API token (Settings page)

Use **Save settings** in the shell after changing panel toggles or opacity on the Overlay page.

## Troubleshooting

| Issue | Fix |
|-------|-----|
| No live data | Confirm you're in a match (not main menu), GSI cfg exists, launch option set |
| Match/player data empty | Requires internet for OpenDota data and avatars; no Steam API key needed |
| Overlay not visible | Use borderless windowed; exclusive fullscreen hides external overlays |
| Firewall prompt | Allow localhost connections for the app |

## Package

Download `MangoSetup-x64.exe` from the [latest GitHub release](https://github.com/cteerakit/mango/releases/latest). Run it to install Mango for your Windows user. It includes the runtimes and adds a Start menu shortcut. No administrator access, PowerShell, or certificate import is required. The unsigned installer may show a Windows security warning, and some security policies block unsigned apps.

In **Settings > App updates**, select **Check for updates**, then **Download update** when a newer installer is available. Run the downloaded installer to update. Settings and notes are kept.

Existing MSIX users should install the EXE version and confirm their settings and notes before removing the old app. See [Publishing Mango installers](docs/automatic-updates.md) for migration details and release setup.

To build the self-contained Windows x64 installer:

```powershell
./scripts/Build-Installer.ps1 -Version 1.0.3.0
```

## Architecture

- `GsiListenerService` — local `HttpListener` on port 40000
- `MatchStore` — normalized live state for UI binding
- Shell pages — Match, Player, Status, Settings
- `OverlayWindow` — transparent WinUI overlay with Win32 click-through
- `OpenDotaService` — cached player enrichment and item metadata

No game memory reading or injection — GSI + public APIs only.

## License

This project is licensed under the [PolyForm Noncommercial License 1.0.0](https://polyformproject.org/licenses/noncommercial/1.0.0/). See [LICENSE](LICENSE) for the full text.
