// Mango — Dota 2 companion. Tauri shell over the `mango-core` logic.
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

#[macro_use]
mod log;
mod app;
mod commands;
mod dota;
mod gsi_server;
mod hotkeys;
mod http;
mod match_store;
mod match_view;
mod mock;
mod opendota;
mod overlay;
mod overlay_state;
mod roster_sources;
mod settings;
mod steam_api;
mod steam_roster;
mod stratz;
mod updater;

use std::sync::Arc;

use tauri::menu::{Menu, MenuItem};
use tauri::tray::{MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent};
use tauri::{Manager, WindowEvent};
use tauri_plugin_global_shortcut::ShortcutState;

use crate::app::AppState;

fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();
    if args.first().map(String::as_str) == Some(steam_roster::HELPER_FLAG) {
        std::process::exit(steam_roster::helper_main(&args[1..]));
    }

    tauri::Builder::default()
        .plugin(tauri_plugin_single_instance::init(|app, _, _| show_main(app)))
        .plugin(tauri_plugin_dialog::init())
        .plugin(tauri_plugin_opener::init())
        .plugin(
            tauri_plugin_global_shortcut::Builder::new()
                .with_handler(|app, shortcut, event| {
                    if event.state() != ShortcutState::Pressed {
                        return;
                    }
                    let Some(state) = app.try_state::<Arc<AppState>>() else { return };
                    let settings = state.settings.read().clone();
                    let state = state.inner().clone();
                    // Window work must not run on the event-loop thread (it deadlocks on Windows).
                    if hotkeys::shortcut_for(&settings.toggle_overlay_hotkey).as_ref() == Some(shortcut) {
                        tauri::async_runtime::spawn(async move { state.toggle_overlay() });
                    } else if hotkeys::shortcut_for(&settings.toggle_interactive_hotkey).as_ref() == Some(shortcut) {
                        tauri::async_runtime::spawn(async move { state.toggle_interactive() });
                    }
                })
                .build(),
        )
        .invoke_handler(tauri::generate_handler![
            commands::app_info,
            commands::reinstall_gsi_config,
            commands::store_state,
            commands::match_view,
            commands::get_settings,
            commands::update_settings,
            commands::notes_list,
            commands::note_save,
            commands::note_delete,
            commands::note_vote,
            commands::note_toggle_toxic,
            commands::notes_export,
            commands::notes_import_pick,
            commands::notes_import_apply,
            commands::rank_options,
            commands::rank_set,
            commands::player_picker,
            commands::local_player,
            commands::player_identity,
            commands::player_profile,
            commands::overlay_set_visible,
            commands::overlay_set_interactive,
            commands::overlay_state,
            commands::overlay_set_regions,
            commands::overlay_save_position,
            commands::overlay_reset_positions,
            commands::hud_font,
            commands::hotkey_describe,
            commands::hotkeys_save,
            commands::update_check,
            commands::update_install,
        ])
        .setup(|app| {
            log::init(&app::local_app_data().join("Mango"));
            log!("Mango {} starting", updater::installed_version());
            let state = AppState::new(app.handle().clone());
            app.manage(state.clone());

            state.install_gsi_config();
            commands::register_hotkeys(&state);
            state.spawn_tickers();
            if state.settings.read().show_mock_data {
                state.store.apply_mock();
            }

            let (port, token) = {
                let s = state.settings.read();
                (s.gsi_port, s.gsi_token.clone())
            };
            let listener = state.clone();
            tauri::async_runtime::spawn(async move {
                let sink = listener.clone();
                let status = gsi_server::start(port, token, Arc::new(move |payload| sink.apply_payload(payload)))
                    .await
                    .unwrap_or_else(|error| error);
                log!("GSI: {status}");
                *listener.gsi_status.lock().unwrap() = status;
                listener.emit_settings();
            });

            build_tray(app)?;
            if let Some(main) = app.get_webview_window("main") {
                let _ = main.show();
            }
            Ok(())
        })
        .on_window_event(|window, event| {
            // Closing the companion window quits Mango, as before; the overlay goes with it.
            if window.label() == "main" {
                if let WindowEvent::CloseRequested { .. } = event {
                    window.app_handle().exit(0);
                }
            }
        })
        .run(tauri::generate_context!())
        .expect("error while running Mango");
}

fn show_main(app: &tauri::AppHandle) {
    if let Some(main) = app.get_webview_window("main") {
        let _ = main.unminimize();
        let _ = main.show();
        let _ = main.set_focus();
    }
}

fn build_tray(app: &tauri::App) -> tauri::Result<()> {
    let show = MenuItem::with_id(app, "show", "Open Mango", true, None::<&str>)?;
    let overlay = MenuItem::with_id(app, "overlay", "Show or hide overlay", true, None::<&str>)?;
    let quit = MenuItem::with_id(app, "quit", "Quit Mango", true, None::<&str>)?;
    let menu = Menu::with_items(app, &[&show, &overlay, &quit])?;
    TrayIconBuilder::with_id("mango")
        .icon(app.default_window_icon().cloned().expect("app icon"))
        .tooltip("Mango")
        .menu(&menu)
        .show_menu_on_left_click(false)
        .on_menu_event(|app, event| match event.id().as_ref() {
            "show" => show_main(app),
            "overlay" => {
                if let Some(state) = app.try_state::<Arc<AppState>>() {
                    let state = state.inner().clone();
                    tauri::async_runtime::spawn(async move { state.toggle_overlay() });
                }
            }
            "quit" => app.exit(0),
            _ => {}
        })
        .on_tray_icon_event(|tray, event| {
            if let TrayIconEvent::Click { button: MouseButton::Left, button_state: MouseButtonState::Up, .. } = event {
                show_main(tray.app_handle());
            }
        })
        .build(app)?;
    Ok(())
}
