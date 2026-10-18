# Mango — Dota 2 Companion App

Windows desktop companion for Dota 2. Mango connects through Valve Game State Integration (GSI) and OpenDota to show a live match roster, player profiles, notes, and an optional in-game overlay.

## Features

**Companion app**

- **Match** — live Radiant/Dire roster with hero, rank, and match counts; OpenDota and Steam enrichment
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
- Steam Web API key (Settings page)

Use **Save settings** in the shell after changing panel toggles or opacity on the Overlay page.

## Troubleshooting

| Issue | Fix |
|-------|-----|
| No live data | Confirm you're in a match (not main menu), GSI cfg exists, launch option set |
| Match/player data empty | Requires internet for OpenDota cache; Steam API key optional for avatars |
| Overlay not visible | Use borderless windowed; exclusive fullscreen hides external overlays |
| Firewall prompt | Allow localhost connections for the app |

## Package

For automatic updates, install the `Mango-<architecture>.appinstaller` asset from the latest GitHub release. Windows checks for updates on launch and every eight hours in the background, then installs updates when Mango is not in use. **Settings > App updates** shows the installed version and lets you check for updates manually.

Existing users who installed a standalone `.msix` must install through the `.appinstaller` once to enroll. Keep the same package identity and signing publisher to preserve settings and player notes. See [Publishing automatic updates](docs/automatic-updates.md) for release setup.

MSIX packaging is enabled via the Windows App SDK project. Publish from Visual Studio (**Package and Publish**) or:

```powershell
dotnet publish src/Mango/Mango.csproj -c Release
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
