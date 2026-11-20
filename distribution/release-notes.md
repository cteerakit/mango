## Changes in v1.1.0.0

- **Rebuilt on a new engine.** Mango is now a single 12 MB app instead of hundreds of runtime files. Everything works as before: Match, You, Player, notes and votes, the overlay, hotkeys, and updates.
- **Your data carries over.** Settings, player notes, votes, toxic marks, manual ranks, overlay positions and hotkeys are read from the same place and kept exactly.
- **More reliable lobby lookup.** Reading the lobby from the local Steam client no longer uses PowerShell, so security policies that block scripts no longer break it.
- **Clearer connection status.** When Dota closes, the status returns to "Waiting for Dota 2" instead of staying on the last match state; the last match stays on the Match page.
- **Overlay build suggestions** now show item names and the items players buy most.
- **Timer alerts** (Developer options) now play a short sound before a rune spawns.
- The overlay closes completely when hidden, so it uses no memory while you play without it.

## Install Mango

Download **MangoSetup-x64.exe**, open it, and follow the setup wizard. Mango installs for your Windows user and adds a Start menu shortcut. No administrator access is required. On older Windows 10 PCs without the Microsoft Edge WebView2 runtime, setup installs it.

This installer is unsigned. Windows may show an unknown-publisher or SmartScreen warning, and some device security policies block unsigned apps.

## Updates

Open **Settings > App updates > Check for updates**. When an update is available, select **Install update and restart**. Mango downloads and verifies the installer, installs it, and reopens. Your settings and player notes are kept.

Windows x64 only.
