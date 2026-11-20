//! The in-game overlay window (C# `OverlayWindowService`, `OverlayClickThroughHelper`).
//!
//! A transparent, borderless, always-on-top window over the primary monitor. It never takes
//! focus from the game, stays out of Alt+Tab, and is clipped to its widgets so empty screen
//! passes clicks to Dota even in interactive mode; when locked, the widgets do too.
//! The window exists only while shown, so a hidden overlay costs no memory.

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Mutex;

use serde::{Deserialize, Serialize};
use tauri::{AppHandle, Manager, PhysicalPosition, PhysicalSize, WebviewUrl, WebviewWindow, WebviewWindowBuilder};

pub const LABEL: &str = "overlay";

/// A widget's bounds in CSS pixels, reported by the overlay page.
#[derive(Debug, Clone, Copy, PartialEq, Deserialize, Serialize)]
pub struct Rect {
    pub x: f64,
    pub y: f64,
    pub width: f64,
    pub height: f64,
}

#[derive(Default)]
pub struct Overlay {
    shown: AtomicBool,
    regions: Mutex<Vec<Rect>>,
}

impl Overlay {
    pub fn is_shown(&self) -> bool {
        self.shown.load(Ordering::SeqCst)
    }

    pub fn window(&self, app: &AppHandle) -> Option<WebviewWindow> {
        app.get_webview_window(LABEL)
    }

    /// Creates the overlay, shown and covering the primary monitor, without activating it.
    ///
    /// Created visible and unfocused so tao itself uses SW_SHOWNOACTIVATE and tracks the
    /// window as visible; showing it later through tao would activate it and steal focus.
    pub fn show(&self, app: &AppHandle, interactive: bool) -> tauri::Result<()> {
        let window = match self.window(app) {
            Some(window) => window,
            None => {
                let mut builder = WebviewWindowBuilder::new(app, LABEL, WebviewUrl::App("overlay.html".into()))
                    .title("Mango Overlay")
                    .transparent(true)
                    .decorations(false)
                    .always_on_top(true)
                    .skip_taskbar(true)
                    .shadow(false)
                    .resizable(false)
                    .focused(false)
                    .focusable(false)
                    .visible(true);
                if let Ok(Some(monitor)) = app.primary_monitor() {
                    let scale = monitor.scale_factor();
                    let (pos, size) = (monitor.position(), monitor.size());
                    builder = builder
                        .position(f64::from(pos.x) / scale, f64::from(pos.y) / scale)
                        .inner_size(f64::from(size.width) / scale, f64::from(size.height) / scale);
                }
                let window = builder.build()?;
                cover_primary_monitor(&window);
                window
            }
        };
        self.shown.store(true, Ordering::SeqCst);
        window.set_ignore_cursor_events(!interactive)?;
        self.refresh_native(&window);
        Ok(())
    }

    /// Hides by closing the window, freeing its WebView.
    pub fn hide(&self, app: &AppHandle) {
        self.shown.store(false, Ordering::SeqCst);
        self.regions.lock().unwrap().clear();
        if let Some(window) = self.window(app) {
            let _ = window.destroy();
        }
    }

    pub fn set_interactive(&self, app: &AppHandle, interactive: bool) {
        if let Some(window) = self.window(app) {
            let _ = window.set_ignore_cursor_events(!interactive);
            self.refresh_native(&window);
        }
    }

    /// Clips the window to the widgets so empty screen never intercepts clicks.
    pub fn set_regions(&self, app: &AppHandle, rects: Vec<Rect>) {
        *self.regions.lock().unwrap() = rects;
        if let Some(window) = self.window(app) {
            self.apply_regions(&window);
        }
    }

    /// Re-asserts styles, region and topmost; Tauri resets styles and games push above us.
    pub fn refresh_native(&self, window: &WebviewWindow) {
        if let Some(hwnd) = hwnd_of(window) {
            native::apply_overlay_styles(hwnd);
            native::reassert_topmost(hwnd);
        }
        self.apply_regions(window);
    }

    fn apply_regions(&self, window: &WebviewWindow) {
        let Some(hwnd) = hwnd_of(window) else { return };
        let scale = window.scale_factor().unwrap_or(1.0);
        let rects: Vec<(i32, i32, i32, i32)> = self
            .regions
            .lock()
            .unwrap()
            .iter()
            .filter(|r| r.width > 0.0 && r.height > 0.0)
            .map(|r| {
                // Slight padding so rounded widget edges stay interactive.
                let left = ((r.x - 2.0).max(0.0) * scale).floor() as i32;
                let top = ((r.y - 2.0).max(0.0) * scale).floor() as i32;
                let right = ((r.x + r.width + 2.0) * scale).ceil() as i32;
                let bottom = ((r.y + r.height + 2.0) * scale).ceil() as i32;
                (left, top, right, bottom)
            })
            .collect();
        native::set_region(hwnd, &rects);
    }
}

fn hwnd_of(window: &WebviewWindow) -> Option<isize> {
    window.hwnd().ok().map(|h| h.0 as isize)
}

/// Full monitor including the taskbar strip, in physical pixels so DPI scaling is exact.
fn cover_primary_monitor(window: &WebviewWindow) {
    if let Ok(Some(monitor)) = window.primary_monitor() {
        let (pos, size) = (monitor.position(), monitor.size());
        let _ = window.set_position(PhysicalPosition::new(pos.x, pos.y));
        let _ = window.set_size(PhysicalSize::new(size.width, size.height));
    }
}

#[cfg(windows)]
mod native {
    use windows_sys::Win32::Foundation::HWND;
    use windows_sys::Win32::Graphics::Gdi::{CombineRgn, CreateRectRgn, DeleteObject, SetWindowRgn, RGN_OR};
    use windows_sys::Win32::UI::WindowsAndMessaging::*;

    /// Never take focus from the game and stay out of Alt+Tab.
    pub fn apply_overlay_styles(hwnd: isize) {
        // SAFETY: plain Win32 style calls on our own window handle.
        unsafe {
            let hwnd = hwnd as HWND;
            let ex = GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
            let wanted = ex | (WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW) as isize;
            if wanted != ex {
                SetWindowLongPtrW(hwnd, GWL_EXSTYLE, wanted);
            }
        }
    }

    pub fn reassert_topmost(hwnd: isize) {
        // SAFETY: as above.
        unsafe {
            SetWindowPos(hwnd as HWND, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        }
    }

    /// Union of `(left, top, right, bottom)` rects; a 1×1 region when there are none.
    pub fn set_region(hwnd: isize, rects: &[(i32, i32, i32, i32)]) {
        // SAFETY: GDI regions we create; ownership passes to the window on success.
        unsafe {
            let region = CreateRectRgn(0, 0, 1, 1);
            if region.is_null() {
                return;
            }
            let mut first = true;
            for &(l, t, r, b) in rects {
                let part = CreateRectRgn(l, t, r, b);
                if part.is_null() {
                    continue;
                }
                if first {
                    CombineRgn(region, part, part, RGN_OR);
                    first = false;
                } else {
                    CombineRgn(region, region, part, RGN_OR);
                }
                DeleteObject(part);
            }
            if SetWindowRgn(hwnd as HWND, region, 1) == 0 {
                DeleteObject(region);
            }
        }
    }
}

#[cfg(not(windows))]
mod native {
    pub fn apply_overlay_styles(_: isize) {}
    pub fn reassert_topmost(_: isize) {}
    pub fn set_region(_: isize, _: &[(i32, i32, i32, i32)]) {}
}
