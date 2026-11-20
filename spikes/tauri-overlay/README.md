# Tauri overlay spike

Throwaway test of whether Tauri 2 can host Mango's in-game overlay well enough to justify
moving the app to Tauri + a web UI. It is **not** wired to GSI; timers and GPM/XPM are fake.

## Run

Requires Rust (stable, MSVC) and Node.

```powershell
npm install
npm run dev     # hot reload: edit src/*.css, src/*.ts, index.html
npm run build   # release exe at src-tauri/target/release/mango-overlay-spike.exe
```

Use `npm run build`, not plain `cargo build --release`: without the Tauri CLI the exe still
points at the dev server and shows a full-screen "can't reach this page".

Hotkeys use **Ctrl+Alt** so the spike can run next to the WinUI Mango (which owns Alt+F9 / Alt+`):

| Hotkey | Action |
|---|---|
| Ctrl+Alt+F9 | Show / hide overlay |
| Ctrl+Alt+` | Toggle interactive mode (drag widgets; positions persist) |

Quit from the tray icon.

## Results so far (automated, on the desktop, 1920×1080 @ 100%)

| Check | Result |
|---|---|
| Transparent full-screen window, widgets with blur | ✅ |
| Click-through when locked, clickable in interactive mode | ✅ (`WS_EX_TRANSPARENT` toggles) |
| Always on top, re-asserted every 2 s | ✅ |
| Never takes focus (startup, hide/show) | ✅ after replacing Tauri's `show()` with `SW_SHOWNOACTIVATE` |
| Hidden from Alt+Tab / taskbar | ✅ `WS_EX_TOOLWINDOW` (re-applied, Tauri resets it) |
| Hotkey taken by another app | ✅ logged, overlay keeps running |
| CPU, locked mode | ~1.6% of one core (host + WebView2) |
| Memory | ~190 MB private across 8 processes (WinUI Mango: ~150 MB) |
| Hot reload | ✅ CSS change visible in <1.5 s, page state kept |
| Exe size | 6.3 MB |

Lessons for a real port:

- Tauri's `show()` activates the window; use `SW_SHOWNOACTIVATE`.
- Tauri rewrites extended styles on show / cursor-event changes; re-apply after each.
- Never leave a `requestAnimationFrame` loop running in the overlay: it repaints at the monitor
  refresh rate (165/s here) and pushed CPU from ~1.6% to ~7–13%.

## Still to test by hand with Dota 2 (Borderless Windowed)

- [ ] Overlay stays above Dota after alt-tabbing in and out, and after a match loads
- [ ] No input lag or stolen clicks in-game while locked
- [ ] Interactive mode: drag widgets, then Ctrl+Alt+` back; Dota gets clicks again
- [ ] Dota FPS / frame time with overlay on vs off (e.g. `cl_showfps 2`)
- [ ] 125% / 150% Windows scaling and a second monitor
