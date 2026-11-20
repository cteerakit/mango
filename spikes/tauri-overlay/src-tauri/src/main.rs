// Tauri overlay spike: a transparent, click-through, always-on-top HUD over Dota 2.
// Hotkeys: Ctrl+Alt+F9 toggles visibility, Ctrl+Alt+` toggles interactive (drag) mode.
// (Ctrl+ added so the spike can run next to the WinUI Mango, which owns Alt+F9 / Alt+`.)
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Mutex;
use std::time::Duration;

use serde::Serialize;
use sysinfo::{Pid, ProcessRefreshKind, ProcessesToUpdate, System};
use tauri::menu::{Menu, MenuItem};
use tauri::tray::TrayIconBuilder;
use tauri::{AppHandle, Emitter, Manager, PhysicalPosition, PhysicalSize, WebviewWindow};
use tauri_plugin_global_shortcut::{Code, GlobalShortcutExt, Modifiers, Shortcut, ShortcutState};

const OVERLAY: &str = "overlay";

static INTERACTIVE: AtomicBool = AtomicBool::new(false);
static VISIBLE: AtomicBool = AtomicBool::new(true);

struct Stats(Mutex<System>);

#[derive(Serialize, Clone)]
struct ProcessStats {
    /// CPU % across the host process and its WebView2 child processes (100% = one core).
    cpu_percent: f32,
    /// Resident memory in MB for the same process set.
    memory_mb: f64,
    process_count: usize,
}

#[derive(Serialize, Clone)]
struct ModeChanged {
    interactive: bool,
}

/// Sums CPU and memory for this process plus every descendant (WebView2 runs out of process).
#[tauri::command]
fn process_stats(stats: tauri::State<Stats>) -> ProcessStats {
    let mut sys = stats.0.lock().unwrap();
    sys.refresh_processes_specifics(
        ProcessesToUpdate::All,
        true,
        ProcessRefreshKind::nothing().with_cpu().with_memory(),
    );

    let root = Pid::from_u32(std::process::id());
    let mut family = vec![root];
    let mut i = 0;
    while i < family.len() {
        let parent = family[i];
        for (pid, p) in sys.processes() {
            if p.parent() == Some(parent) && !family.contains(pid) {
                family.push(*pid);
            }
        }
        i += 1;
    }

    let (mut cpu, mut mem) = (0f32, 0u64);
    for pid in &family {
        if let Some(p) = sys.process(*pid) {
            cpu += p.cpu_usage();
            mem += p.memory();
        }
    }

    ProcessStats {
        cpu_percent: cpu,
        memory_mb: mem as f64 / 1_048_576.0,
        process_count: family.len(),
    }
}

#[cfg(windows)]
mod native {
    use windows_sys::Win32::Foundation::HWND;
    use windows_sys::Win32::UI::WindowsAndMessaging::*;

    /// Never take focus from the game and stay out of Alt+Tab.
    pub fn apply_overlay_styles(hwnd: isize) {
        unsafe {
            let hwnd = hwnd as HWND;
            let ex = GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
            let ex = ex | (WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW) as isize;
            SetWindowLongPtrW(hwnd, GWL_EXSTYLE, ex);
        }
    }

    /// Tauri's show() uses SW_SHOW, which activates the window and pulls focus out of the game.
    pub fn show_without_focus(hwnd: isize) {
        unsafe {
            ShowWindow(hwnd as HWND, SW_SHOWNOACTIVATE);
        }
    }

    /// Re-assert HWND_TOPMOST without activating; games can push themselves above us.
    pub fn reassert_topmost(hwnd: isize) {
        unsafe {
            SetWindowPos(
                hwnd as HWND,
                HWND_TOPMOST,
                0,
                0,
                0,
                0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER,
            );
        }
    }
}

fn overlay(app: &AppHandle) -> WebviewWindow {
    app.get_webview_window(OVERLAY).expect("overlay window")
}

fn hwnd_of(window: &WebviewWindow) -> Option<isize> {
    window.hwnd().ok().map(|h| h.0 as isize)
}

/// Covers the monitor Dota is most likely on (primary), in physical pixels so DPI scaling is exact.
fn cover_primary_monitor(window: &WebviewWindow) {
    if let Ok(Some(monitor)) = window.primary_monitor() {
        let pos = monitor.position();
        let size = monitor.size();
        let _ = window.set_position(PhysicalPosition::new(pos.x, pos.y));
        let _ = window.set_size(PhysicalSize::new(size.width, size.height));
    }
}

/// Tauri rewrites the extended styles when it shows the window or toggles cursor events,
/// so re-apply ours (no-activate, tool window) and topmost after each of those.
fn refresh_native(window: &WebviewWindow) {
    if let Some(h) = hwnd_of(window) {
        native::apply_overlay_styles(h);
        native::reassert_topmost(h);
    }
}

fn show_overlay(window: &WebviewWindow) {
    refresh_native(window);
    if let Some(h) = hwnd_of(window) {
        native::show_without_focus(h);
    }
    refresh_native(window);
}

fn set_interactive(app: &AppHandle, interactive: bool) {
    INTERACTIVE.store(interactive, Ordering::SeqCst);
    let window = overlay(app);
    let _ = window.set_ignore_cursor_events(!interactive);
    refresh_native(&window);
    let _ = app.emit("mode-changed", ModeChanged { interactive });
}

fn toggle_visible(app: &AppHandle) {
    let window = overlay(app);
    let visible = !VISIBLE.load(Ordering::SeqCst);
    VISIBLE.store(visible, Ordering::SeqCst);
    if visible {
        show_overlay(&window);
    } else {
        // Leaving interactive mode on a hidden window would swallow clicks when it reappears.
        set_interactive(app, false);
        let _ = window.hide();
    }
}

fn main() {
    let toggle_overlay = Shortcut::new(Some(Modifiers::CONTROL | Modifiers::ALT), Code::F9);
    let toggle_mode = Shortcut::new(Some(Modifiers::CONTROL | Modifiers::ALT), Code::Backquote);

    tauri::Builder::default()
        .plugin(
            tauri_plugin_global_shortcut::Builder::new()
                .with_handler(move |app, shortcut, event| {
                    if event.state() != ShortcutState::Pressed {
                        return;
                    }
                    if shortcut == &toggle_overlay {
                        toggle_visible(app);
                    } else if shortcut == &toggle_mode && VISIBLE.load(Ordering::SeqCst) {
                        let next = !INTERACTIVE.load(Ordering::SeqCst);
                        set_interactive(app, next);
                    }
                })
                .build(),
        )
        .manage(Stats(Mutex::new(System::new().expect("sysinfo"))))
        .invoke_handler(tauri::generate_handler![process_stats])
        .setup(move |app| {
            let handle = app.handle().clone();
            // A hotkey owned by another app (e.g. the WinUI Mango) must not kill the overlay.
            for shortcut in [toggle_overlay, toggle_mode] {
                if let Err(e) = handle.global_shortcut().register(shortcut) {
                    eprintln!("hotkey {shortcut:?} unavailable: {e}");
                }
            }

            let window = overlay(&handle);
            cover_primary_monitor(&window);
            window.set_ignore_cursor_events(true)?;
            show_overlay(&window);

            // Topmost watchdog, same idea as the WinUI app's ScheduleNativeStateRefresh.
            let watchdog = handle.clone();
            std::thread::spawn(move || loop {
                std::thread::sleep(Duration::from_secs(2));
                if VISIBLE.load(Ordering::SeqCst) {
                    refresh_native(&overlay(&watchdog));
                }
            });

            let quit = MenuItem::with_id(app, "quit", "Quit overlay spike", true, None::<&str>)?;
            let menu = Menu::with_items(app, &[&quit])?;
            TrayIconBuilder::new()
                .icon(app.default_window_icon().unwrap().clone())
                .tooltip("Mango overlay spike (Ctrl+Alt+F9 / Ctrl+Alt+`)")
                .menu(&menu)
                .on_menu_event(|app, e| {
                    if e.id() == "quit" {
                        app.exit(0);
                    }
                })
                .build(app)?;

            Ok(())
        })
        .run(tauri::generate_context!())
        .expect("error while running overlay spike");
}
