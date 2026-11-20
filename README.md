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

- Windows 10 19041+ / Windows 11 (WebView2 runtime; the installer adds it when missing)
- Dota 2 installed via Steam
- Dota running in **Borderless Windowed** (not exclusive fullscreen)
- Steam launch option: `-gamestateintegration`

For development: [Rust](https://rustup.rs) (stable, MSVC) and Node.js 22+.

## Quick start (development)

```powershell
cd app
npm install
npm run dev
```

`npm run dev` starts the Vite dev server and opens Mango. **Edits to anything under `app/src` (React, CSS) appear in the running app immediately**, without a restart or losing the current page. Rust changes under `app/src-tauri` or `app/crates` rebuild and restart the app automatically.

On first launch, Mango installs `gamestate_integration_mango.cfg` into your Dota `cfg/gamestate_integration/` folder. Start a match — live data arrives on `http://127.0.0.1:40000/`.

### Work with mock data

Open **About > Open developer options** and turn on **Show mock data**. Mango shows a sample match, players, and overlay without Dota, so you can work on the UI with hot reload. The setting is saved across restarts.

You can also run just the UI in a browser with `npm run vite` (http://localhost:1420). Data calls return empty defaults there; the overlay page (`/overlay.html`) shows a preview.

### Tests

```powershell
cd app
npm test                 # UI helpers (Vitest)
npx vite build           # the Rust crate embeds the built UI
cargo test --workspace   # game logic, services, settings compatibility
```

## Hotkeys

| Hotkey | Action |
|--------|--------|
| `Alt+F9` | Toggle overlay visibility |
| `Alt+`` | Toggle interactive mode (drag panels) |

Change them on the **Shortcuts** page.

## Settings

Stored in `%LOCALAPPDATA%\Mango\settings.json` — the same file and format as the earlier WinUI app, so settings and notes carry over in both directions:

- Overlay panel visibility, opacity, and layout positions
- Turbo mode timer rules
- GSI auth token
- Optional Steam Web API key and STRATZ API token (Settings page)
- Player notes, votes, toxic marks, and manual ranks

Changes save as you make them. Diagnostics go to `%LOCALAPPDATA%\Mango\mango.log`.

## Troubleshooting

| Issue | Fix |
|-------|-----|
| No live data | Confirm you're in a match (not main menu), GSI cfg exists, launch option set |
| "Port 40000 is in use" on Status | Close any other Mango (for example an older install) and restart Mango |
| Match/player data empty | Requires internet for OpenDota data and avatars; add a Steam Web API key in Settings for live teams and heroes |
| Overlay not visible | Use borderless windowed; exclusive fullscreen hides external overlays |
| Hotkey does nothing | Another app may own it — see Status, or pick another combination on Shortcuts |

## Package

Download `MangoSetup-x64.exe` from the [latest GitHub release](https://github.com/cteerakit/mango/releases/latest). Run it to install Mango for your Windows user and add a Start menu shortcut. No administrator access is required. The unsigned installer may show a Windows security warning, and some security policies block unsigned apps.

In **Settings > App updates**, select **Check for updates**, then **Install update and restart** when a newer version is available. Mango downloads and verifies the installer, installs it, and reopens. Settings and notes are kept. **Download installer manually** is available as a fallback. See [Publishing Mango installers](docs/automatic-updates.md).

To build the Windows x64 installer (versions end in `.0`):

```powershell
./scripts/Build-Installer.ps1 -Version 1.1.0.0
```

## Architecture

Mango is a [Tauri 2](https://tauri.app) app: a Rust backend with a React + TypeScript + Tailwind UI rendered by WebView2.

- `app/crates/mango-core` — game logic with no I/O: GSI parsing and normalization, roster merging, match-page state, notes, ranks and rank estimates, rune timers, Steam mappers
- `app/src-tauri` — the app: GSI listener (axum on `127.0.0.1:40000`), OpenDota/STRATZ/Steam clients with a file cache, live roster resolution, settings, the overlay window (Win32 click-through and regions), hotkeys, updater
- `app/src` — the companion UI (`index.html`) and the overlay page (`overlay.html`)

The local Steam roster runs Steam's API in a short-lived `Mango.exe --steam-roster` helper process, never in the app itself.

No game memory reading or injection — GSI + public APIs only.

The previous WinUI 3 / C# app is still in `src/` and `tests/` for reference while the Tauri app is validated in game.

## License

This project is licensed under the [PolyForm Noncommercial License 1.0.0](https://polyformproject.org/licenses/noncommercial/1.0.0/). See [LICENSE](LICENSE) for the full text.
