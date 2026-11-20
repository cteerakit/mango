//! Shared app state and the event fan-out to the windows.

use std::path::PathBuf;
use std::sync::{Arc, Mutex};
use std::time::Duration;

use mango_core::gsi::GsiPayload;
use serde::Serialize;
use tauri::{AppHandle, Emitter};
use tokio::sync::mpsc;

use crate::dota::{self, GsiInstallResult};
use crate::hotkeys;
use crate::http::{ReqwestHttp, SharedHttp};
use crate::match_store::MatchStore;
use crate::match_view::MatchView;
use crate::mock;
use crate::opendota::OpenDota;
use crate::overlay::{self, Overlay};
use crate::overlay_state::{Clock, OverlayView};
use crate::roster_sources::LiveRosterResolver;
use crate::settings::{AppSettings, HotkeyBinding, SettingsStore};
use crate::steam_api::SteamApi;
use crate::steam_roster::LocalSteamRoster;
use crate::stratz::Stratz;

pub const EVENT_STORE: &str = "store-state";
pub const EVENT_MATCH_VIEW: &str = "match-view";
pub const EVENT_OVERLAY: &str = "overlay-state";
pub const EVENT_SETTINGS: &str = "settings";
pub const EVENT_NOTES: &str = "notes-changed";

pub struct AppState {
    pub app: AppHandle,
    pub settings: Arc<SettingsStore>,
    pub http: SharedHttp,
    pub opendota: Arc<OpenDota>,
    pub stratz: Arc<Stratz>,
    pub store: Arc<MatchStore>,
    pub match_view: Arc<MatchView>,
    pub overlay: Overlay,
    pub clock: Mutex<Clock>,
    /// `(hero, items)` for the overlay build panel.
    pub build_items: Mutex<(String, Vec<String>)>,
    pub gsi_status: Mutex<String>,
    pub gsi_install: Mutex<GsiInstallResult>,
    pub hotkey_status: Mutex<String>,
    /// Notes parsed by the import picker, waiting for merge/replace.
    pub pending_import: Mutex<Option<mango_core::notes::NotesMap>>,
    pub available_update: Mutex<Option<crate::updater::UpdateCheck>>,
}

/// Settings for the UI: everything except notes and ranks, which have their own commands.
#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SettingsView {
    pub gsi_port: u16,
    pub theme: u8,
    pub show_player_panel: bool,
    pub show_bounty_timer: bool,
    pub show_power_timer: bool,
    pub show_wisdom_timer: bool,
    pub show_lotus_timer: bool,
    pub show_build_panel: bool,
    pub overlay_visible: bool,
    pub overlay_interactive: bool,
    pub overlay_opacity: f64,
    pub timer_sounds_enabled: bool,
    pub is_turbo_mode: bool,
    pub rune_timer_lead_seconds: i32,
    pub show_mock_data: bool,
    pub steam_api_key: String,
    pub stratz_api_token: String,
    pub toggle_overlay_hotkey: HotkeyView,
    pub toggle_interactive_hotkey: HotkeyView,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct HotkeyView {
    pub ctrl: bool,
    pub alt: bool,
    pub shift: bool,
    pub win: bool,
    pub virtual_key: u32,
    pub display: String,
}

impl From<&HotkeyBinding> for HotkeyView {
    fn from(b: &HotkeyBinding) -> Self {
        Self { ctrl: b.ctrl, alt: b.alt, shift: b.shift, win: b.win, virtual_key: b.virtual_key, display: hotkeys::display(b) }
    }
}

impl From<&AppSettings> for SettingsView {
    fn from(s: &AppSettings) -> Self {
        Self {
            gsi_port: s.gsi_port,
            theme: s.theme,
            show_player_panel: s.show_player_panel,
            show_bounty_timer: s.show_bounty_timer,
            show_power_timer: s.show_power_timer,
            show_wisdom_timer: s.show_wisdom_timer,
            show_lotus_timer: s.show_lotus_timer,
            show_build_panel: s.show_build_panel,
            overlay_visible: s.overlay_visible,
            overlay_interactive: s.overlay_interactive,
            overlay_opacity: s.overlay_opacity,
            timer_sounds_enabled: s.timer_sounds_enabled,
            is_turbo_mode: s.is_turbo_mode,
            rune_timer_lead_seconds: s.rune_timer_lead_seconds,
            show_mock_data: s.show_mock_data,
            steam_api_key: s.steam_api_key.clone(),
            stratz_api_token: s.stratz_api_token.clone(),
            toggle_overlay_hotkey: (&s.toggle_overlay_hotkey).into(),
            toggle_interactive_hotkey: (&s.toggle_interactive_hotkey).into(),
        }
    }
}

pub fn local_app_data() -> PathBuf {
    std::env::var_os("LOCALAPPDATA").map(PathBuf::from).unwrap_or_else(std::env::temp_dir)
}

impl AppState {
    pub fn new(app: AppHandle) -> Arc<Self> {
        let settings = Arc::new(SettingsStore::load(&local_app_data()));
        // Never restore a blocking overlay across sessions; the user turns it on.
        settings.update(|s| {
            s.overlay_visible = false;
            s.overlay_interactive = false;
        });
        let cache = settings.directory().join("cache");
        let http: SharedHttp = Arc::new(ReqwestHttp::new());
        let opendota = Arc::new(OpenDota::new(http.clone(), cache.clone()));
        let stratz = Arc::new(Stratz::new(http.clone(), cache));
        let resolver = Arc::new(LiveRosterResolver {
            settings: settings.clone(),
            opendota: opendota.clone(),
            steam: Arc::new(SteamApi::new(http.clone())),
            local: Arc::new(LocalSteamRoster::new(dota::find_install_paths())),
        });

        // Store changes are handled on one task so they never re-enter the store.
        let (changed_tx, changed_rx) = mpsc::unbounded_channel::<()>();
        let store = MatchStore::new(resolver, mock::snapshot, move || {
            let _ = changed_tx.send(());
        });
        let view_app = app.clone();
        let match_view = MatchView::new(store.clone(), settings.clone(), opendota.clone(), stratz.clone(), move |view| {
            let _ = view_app.emit(EVENT_MATCH_VIEW, view);
        });

        let state = Arc::new(Self {
            app,
            settings,
            http,
            opendota,
            stratz,
            store,
            match_view,
            overlay: Overlay::default(),
            clock: Mutex::new(Clock::default()),
            build_items: Mutex::new((String::new(), Vec::new())),
            gsi_status: Mutex::new("Starting…".into()),
            gsi_install: Mutex::new(GsiInstallResult::default()),
            hotkey_status: Mutex::new(String::new()),
            pending_import: Mutex::new(None),
            available_update: Mutex::new(None),
        });
        state.clone().spawn_store_listener(changed_rx);
        state
    }

    fn spawn_store_listener(self: Arc<Self>, mut changed: mpsc::UnboundedReceiver<()>) {
        tauri::async_runtime::spawn(async move {
            while changed.recv().await.is_some() {
                while changed.try_recv().is_ok() {}
                self.on_store_changed();
            }
        });
    }

    fn on_store_changed(self: &Arc<Self>) {
        let state = self.store.state();
        self.clock.lock().unwrap().observe(&state.snapshot);
        // Remember the user's account for the You page and encounter history.
        let gsi_id = state.snapshot.steam_id.trim().to_string();
        if !state.is_using_mock_data && !gsi_id.is_empty() && self.settings.read().steam_id.as_deref() != Some(gsi_id.as_str()) {
            self.settings.update(|s| s.steam_id = Some(gsi_id));
        }
        let _ = self.app.emit(EVENT_STORE, &state);
        self.match_view.refresh();
        self.refresh_build_items(&state.snapshot.hero_name, state.is_using_mock_data);
        self.emit_overlay();
    }

    fn refresh_build_items(self: &Arc<Self>, hero: &str, is_mock: bool) {
        let key = if is_mock { "__mock__".to_string() } else { hero.to_string() };
        {
            let mut current = self.build_items.lock().unwrap();
            if current.0 == key {
                return;
            }
            *current = (key.clone(), if is_mock { mock::build_suggestions() } else { Vec::new() });
        }
        if is_mock || hero.trim().is_empty() {
            return;
        }
        let state = Arc::clone(self);
        tauri::async_runtime::spawn(async move {
            let items = state.opendota.suggested_items(&key).await;
            let mut current = state.build_items.lock().unwrap();
            if current.0 == key {
                current.1 = items;
            }
        });
    }

    pub fn apply_payload(&self, payload: GsiPayload) {
        self.store.apply_payload(payload);
    }

    pub fn overlay_view(&self) -> OverlayView {
        let state = self.store.state();
        let settings = self.settings.read().clone();
        let items = self.build_items.lock().unwrap().1.clone();
        let hint = hotkeys::display(&settings.toggle_interactive_hotkey);
        self.clock.lock().unwrap().build_view(&state.snapshot, &settings, state.is_using_mock_data, items, hint)
    }

    pub fn emit_overlay(&self) {
        if self.overlay.is_shown() {
            let _ = self.app.emit_to(overlay::LABEL, EVENT_OVERLAY, self.overlay_view());
        }
    }

    pub fn emit_settings(&self) {
        let view = SettingsView::from(&*self.settings.read());
        let _ = self.app.emit(EVENT_SETTINGS, view);
    }

    pub fn emit_notes_changed(self: &Arc<Self>) {
        let _ = self.app.emit(EVENT_NOTES, ());
        self.match_view.refresh();
    }

    pub fn set_overlay_visible(&self, visible: bool) {
        let interactive = self.settings.read().overlay_interactive;
        if visible {
            if let Err(error) = self.overlay.show(&self.app, interactive) {
                crate::log!("overlay could not open: {error}");
                return;
            }
        } else {
            self.overlay.hide(&self.app);
        }
        self.settings.update(|s| {
            s.overlay_visible = visible;
            if !visible {
                // Leaving interactive mode on a hidden overlay would swallow clicks when it returns.
                s.overlay_interactive = false;
            }
        });
        self.emit_settings();
        self.emit_overlay();
    }

    pub fn toggle_overlay(&self) {
        let visible = self.settings.read().overlay_visible;
        self.set_overlay_visible(!visible);
    }

    pub fn set_interactive(&self, interactive: bool) {
        if !self.settings.read().overlay_visible {
            return;
        }
        self.settings.update(|s| s.overlay_interactive = interactive);
        self.overlay.set_interactive(&self.app, interactive);
        self.emit_settings();
        self.emit_overlay();
    }

    pub fn toggle_interactive(&self) {
        let interactive = self.settings.read().overlay_interactive;
        self.set_interactive(!interactive);
    }

    pub fn apply_mock_mode(self: &Arc<Self>) {
        if self.settings.read().show_mock_data {
            self.store.apply_mock();
        } else {
            self.store.clear_mock();
        }
    }

    pub fn install_gsi_config(&self) -> GsiInstallResult {
        let result = dota::install_config(&self.settings.read());
        *self.gsi_install.lock().unwrap() = result.clone();
        result
    }

    /// Background loops: stale-connection check and the overlay's once-a-second refresh.
    pub fn spawn_tickers(self: &Arc<Self>) {
        let state = Arc::clone(self);
        tauri::async_runtime::spawn(async move {
            let mut tick = tokio::time::interval(Duration::from_secs(1));
            let mut seconds = 0u64;
            loop {
                tick.tick().await;
                seconds += 1;
                if seconds.is_multiple_of(5) {
                    state.store.check_stale();
                }
                if state.overlay.is_shown() {
                    state.emit_overlay();
                    if seconds.is_multiple_of(2) {
                        if let Some(window) = state.overlay.window(&state.app) {
                            state.overlay.refresh_native(&window);
                        }
                    }
                }
            }
        });
    }
}
