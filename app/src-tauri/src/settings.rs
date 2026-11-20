//! `%LOCALAPPDATA%\Mango\settings.json`, shared with the WinUI app (C# `AppSettings`).
//!
//! Field names and value shapes match System.Text.Json's defaults (PascalCase, enums as
//! numbers), and unknown fields are kept, so either app can read what the other wrote.

use std::fs;
use std::path::{Path, PathBuf};
use std::sync::RwLock;

use indexmap::IndexMap;
use mango_core::notes::{self, NotesMap};
use mango_core::ranks::{self, RankOverrides};
use serde::{Deserialize, Serialize};
use serde_json::Value;

pub const DEFAULT_GSI_PORT: u16 = 40000;
const FILE_NAME: &str = "settings.json";
/// Package family of the old MSIX build, whose settings are imported once.
const MSIX_PACKAGE: &str = "D9984A3D-08FB-4EF1-BEF8-0B6EE6E0A12D_1z32rh13vfry6";

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Default)]
#[serde(rename_all = "PascalCase", default)]
pub struct HotkeyBinding {
    pub ctrl: bool,
    pub alt: bool,
    pub shift: bool,
    pub win: bool,
    /// Windows virtual-key code; 0 = not set.
    pub virtual_key: u32,
}

impl HotkeyBinding {
    pub const fn default_toggle_overlay() -> Self {
        Self { ctrl: false, alt: true, shift: false, win: false, virtual_key: 0x78 } // Alt+F9
    }

    pub const fn default_toggle_interactive() -> Self {
        Self { ctrl: false, alt: true, shift: false, win: false, virtual_key: 0xC0 } // Alt+`
    }

    pub fn has_modifier(&self) -> bool {
        self.ctrl || self.alt || self.shift || self.win
    }
}

/// Remembered widget positions in DIPs; `None` means never dragged, so the default anchor applies.
#[derive(Debug, Clone, Copy, PartialEq, Serialize, Deserialize, Default)]
#[serde(rename_all = "PascalCase", default)]
pub struct PanelLayout {
    pub player_x: Option<f64>,
    pub player_y: Option<f64>,
    pub bounty_x: Option<f64>,
    pub bounty_y: Option<f64>,
    pub power_x: Option<f64>,
    pub power_y: Option<f64>,
    pub wisdom_x: Option<f64>,
    pub wisdom_y: Option<f64>,
    pub lotus_x: Option<f64>,
    pub lotus_y: Option<f64>,
    pub build_x: Option<f64>,
    pub build_y: Option<f64>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase", default)]
pub struct AppSettings {
    pub gsi_port: u16,
    /// 0 = System, 1 = Light, 2 = Dark.
    pub theme: u8,
    pub gsi_token: String,
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
    /// Seconds before a rune spawns that its countdown appears.
    pub rune_timer_lead_seconds: i32,
    /// Stored under the WinUI app's old name so developer settings carry over.
    #[serde(rename = "DebugOverlayPreview")]
    pub show_mock_data: bool,
    pub steam_id: Option<String>,
    /// Steam Web API key for live roster teams and heroes.
    pub steam_api_key: String,
    /// STRATZ token for replay-analysed positions.
    pub stratz_api_token: String,
    pub player_notes: NotesMap,
    pub player_ranks: RankOverrides,
    pub toggle_overlay_hotkey: HotkeyBinding,
    pub toggle_interactive_hotkey: HotkeyBinding,
    pub panel_layout: PanelLayout,
    /// Fields this version does not know, preserved on save.
    #[serde(flatten)]
    pub extra: IndexMap<String, Value>,
}

impl Default for AppSettings {
    fn default() -> Self {
        Self {
            gsi_port: DEFAULT_GSI_PORT,
            theme: 0,
            gsi_token: String::new(),
            show_player_panel: true,
            show_bounty_timer: true,
            show_power_timer: true,
            show_wisdom_timer: true,
            show_lotus_timer: true,
            show_build_panel: true,
            overlay_visible: false,
            overlay_interactive: false,
            overlay_opacity: 0.92,
            timer_sounds_enabled: true,
            is_turbo_mode: false,
            rune_timer_lead_seconds: 15,
            show_mock_data: false,
            steam_id: None,
            steam_api_key: String::new(),
            stratz_api_token: String::new(),
            player_notes: NotesMap::new(),
            player_ranks: RankOverrides::new(),
            toggle_overlay_hotkey: HotkeyBinding::default_toggle_overlay(),
            toggle_interactive_hotkey: HotkeyBinding::default_toggle_interactive(),
            panel_layout: PanelLayout::default(),
            extra: IndexMap::new(),
        }
    }
}

impl AppSettings {
    pub fn normalize(&mut self) {
        if self.theme > 2 {
            self.theme = 0;
        }
        if self.gsi_port == 0 {
            self.gsi_port = DEFAULT_GSI_PORT;
        }
        if self.toggle_overlay_hotkey.virtual_key == 0 {
            self.toggle_overlay_hotkey = HotkeyBinding::default_toggle_overlay();
        }
        if self.toggle_interactive_hotkey.virtual_key == 0 {
            self.toggle_interactive_hotkey = HotkeyBinding::default_toggle_interactive();
        }
        self.player_notes = notes::normalize_stored(&self.player_notes);
        self.player_ranks = ranks::normalize(Some(std::mem::take(&mut self.player_ranks)));
        self.rune_timer_lead_seconds = self.rune_timer_lead_seconds.clamp(5, 120);
        if !self.overlay_opacity.is_finite() {
            self.overlay_opacity = 0.92;
        }
        self.overlay_opacity = self.overlay_opacity.clamp(0.4, 1.0);
        self.steam_api_key = self.steam_api_key.trim().to_string();
        self.stratz_api_token = self.stratz_api_token.trim().to_string();
    }

    fn with_new_token() -> Self {
        let mut bytes = [0u8; 16];
        let _ = getrandom::fill(&mut bytes);
        Self { gsi_token: hex::encode(bytes), ..Self::default() }
    }
}

/// Thread-safe settings with their file.
pub struct SettingsStore {
    path: PathBuf,
    settings: RwLock<AppSettings>,
}

impl SettingsStore {
    /// Loads (or creates) settings in `directory`, importing the MSIX or legacy APEM copy once.
    pub fn load(local_app_data: &Path) -> Self {
        let directory = local_app_data.join("Mango");
        let path = directory.join(FILE_NAME);
        import_msix_settings(local_app_data);

        let legacy = local_app_data.join("APEM").join(FILE_NAME);
        let source = if path.exists() { Some(path.clone()) } else if legacy.exists() { Some(legacy) } else { None };

        let settings = match source.as_deref().map(read_settings) {
            Some(Ok(mut settings)) => {
                settings.normalize();
                settings
            }
            Some(Err(error)) => {
                // Never overwrite a file we could not read: keep a copy for recovery.
                crate::log!("settings unreadable ({error}); starting from defaults");
                if let Some(source) = &source {
                    let backup = directory.join(format!("settings.unreadable-{}.json", chrono::Utc::now().timestamp()));
                    let _ = fs::copy(source, backup);
                }
                AppSettings::with_new_token()
            }
            None => AppSettings::with_new_token(),
        };

        let store = Self { path, settings: RwLock::new(settings) };
        if source.is_none() || store.read().gsi_token.is_empty() {
            store.update(|s| {
                if s.gsi_token.is_empty() {
                    s.gsi_token = AppSettings::with_new_token().gsi_token;
                }
            });
        }
        store
    }

    pub fn read(&self) -> std::sync::RwLockReadGuard<'_, AppSettings> {
        self.settings.read().unwrap_or_else(|p| p.into_inner())
    }

    /// Changes settings and saves them. Save failures are reported but keep the change in memory.
    pub fn update<R>(&self, change: impl FnOnce(&mut AppSettings) -> R) -> R {
        let (result, snapshot) = {
            let mut settings = self.settings.write().unwrap_or_else(|p| p.into_inner());
            let result = change(&mut settings);
            settings.normalize();
            (result, settings.clone())
        };
        if let Err(error) = write_settings(&self.path, &snapshot) {
            crate::log!("could not save settings: {error}");
        }
        result
    }

    /// Like [`update`](Self::update), but returns the save error so callers can undo.
    pub fn try_update<R>(&self, change: impl FnOnce(&mut AppSettings) -> R) -> anyhow::Result<R> {
        let mut settings = self.settings.write().unwrap_or_else(|p| p.into_inner());
        let mut next = settings.clone();
        let result = change(&mut next);
        next.normalize();
        write_settings(&self.path, &next)?;
        *settings = next;
        Ok(result)
    }

    pub fn directory(&self) -> &Path {
        self.path.parent().expect("settings path has a directory")
    }
}

fn read_settings(path: &Path) -> anyhow::Result<AppSettings> {
    let text = fs::read_to_string(path)?;
    Ok(serde_json::from_str(text.trim_start_matches('\u{feff}'))?)
}

/// Writes atomically: a crash mid-write leaves the previous file intact.
fn write_settings(path: &Path, settings: &AppSettings) -> anyhow::Result<()> {
    if let Some(directory) = path.parent() {
        fs::create_dir_all(directory)?;
    }
    let temp = path.with_extension("json.tmp");
    fs::write(&temp, serde_json::to_string_pretty(settings)?)?;
    fs::rename(&temp, path)?;
    Ok(())
}

/// The MSIX build kept settings in its package folder; copy them over once (C# `SettingsMigration`).
pub fn import_msix_settings(local_app_data: &Path) {
    let destination = local_app_data.join("Mango").join(FILE_NAME);
    if destination.exists() {
        return;
    }
    let source = local_app_data
        .join("Packages")
        .join(MSIX_PACKAGE)
        .join("LocalCache")
        .join("Local")
        .join("Mango")
        .join(FILE_NAME);
    if source.exists() {
        if let Some(directory) = destination.parent() {
            let _ = fs::create_dir_all(directory);
        }
        let _ = fs::copy(source, destination);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const WINUI_SETTINGS: &str = r#"{
  "GsiPort": 40000,
  "Theme": 2,
  "GsiToken": "abc",
  "ShowPlayerPanel": false,
  "OverlayOpacity": 0.8,
  "RuneTimerLeadSeconds": 500,
  "DebugOverlayPreview": true,
  "SteamId": null,
  "SteamApiKey": "  key  ",
  "PlayerNotes": { "76561198000000000": { "PlayerName": "Invoker", "Content": "mid", "LikeCount": 1 } },
  "PlayerRanks": { "76561197960278073": { "PlayerId": "12345", "PlayerName": "P", "RankTier": 54 } },
  "ToggleOverlayHotkey": { "Ctrl": false, "Alt": true, "Shift": false, "Win": false, "VirtualKey": 120 },
  "ToggleInteractiveHotkey": { "Ctrl": false, "Alt": true, "Shift": false, "Win": false, "VirtualKey": 0 },
  "PanelLayout": { "PlayerX": 10.5, "PlayerY": null },
  "FutureSetting": { "x": 1 }
}"#;

    #[test]
    fn reads_winui_settings_and_normalizes() {
        let mut settings: AppSettings = serde_json::from_str(WINUI_SETTINGS).unwrap();
        settings.normalize();
        assert_eq!(settings.theme, 2);
        assert!(!settings.show_player_panel);
        assert!(settings.show_bounty_timer, "missing fields keep defaults");
        assert!(settings.show_mock_data);
        assert_eq!(settings.rune_timer_lead_seconds, 120);
        assert_eq!(settings.steam_api_key, "key");
        assert_eq!(settings.player_notes.get("76561198000000000").unwrap().player_id, "76561198000000000");
        assert_eq!(settings.player_ranks["12345"].rank_tier, 54, "ranks re-keyed by account id");
        assert_eq!(settings.toggle_interactive_hotkey, HotkeyBinding::default_toggle_interactive());
        assert_eq!(settings.panel_layout.player_x, Some(10.5));
    }

    #[test]
    fn writes_the_winui_shape_and_keeps_unknown_fields() {
        let settings: AppSettings = serde_json::from_str(WINUI_SETTINGS).unwrap();
        let json: Value = serde_json::to_value(&settings).unwrap();
        assert_eq!(json["DebugOverlayPreview"], true);
        assert_eq!(json["ToggleOverlayHotkey"]["VirtualKey"], 120);
        assert_eq!(json["PlayerNotes"]["76561198000000000"]["PlayerName"], "Invoker");
        assert_eq!(json["FutureSetting"]["x"], 1);
        assert!(json.get("ShowMockData").is_none());
    }

    #[test]
    fn load_creates_defaults_with_a_token_and_saves_atomically() {
        let root = tempfile::tempdir().unwrap();
        let store = SettingsStore::load(root.path());
        assert_eq!(store.read().gsi_token.len(), 32);
        assert!(root.path().join("Mango").join("settings.json").exists());

        store.update(|s| s.is_turbo_mode = true);
        let reloaded = SettingsStore::load(root.path());
        assert!(reloaded.read().is_turbo_mode);
        assert_eq!(reloaded.read().gsi_token, store.read().gsi_token);
    }

    #[test]
    fn unreadable_settings_are_backed_up_not_overwritten() {
        let root = tempfile::tempdir().unwrap();
        let dir = root.path().join("Mango");
        fs::create_dir_all(&dir).unwrap();
        fs::write(dir.join("settings.json"), "{ broken").unwrap();

        let _store = SettingsStore::load(root.path());

        let backups: Vec<_> = fs::read_dir(&dir)
            .unwrap()
            .filter_map(|e| e.ok())
            .filter(|e| e.file_name().to_string_lossy().starts_with("settings.unreadable-"))
            .collect();
        assert_eq!(backups.len(), 1);
        assert_eq!(fs::read_to_string(backups[0].path()).unwrap(), "{ broken");
    }

    #[test]
    fn imports_msix_settings_once_and_keeps_existing() {
        let root = tempfile::tempdir().unwrap();
        let source = root.path().join("Packages").join(MSIX_PACKAGE).join("LocalCache/Local/Mango");
        fs::create_dir_all(&source).unwrap();
        fs::write(source.join("settings.json"), r#"{"Theme":2}"#).unwrap();

        import_msix_settings(root.path());
        let destination = root.path().join("Mango/settings.json");
        assert_eq!(fs::read_to_string(&destination).unwrap(), r#"{"Theme":2}"#);

        fs::write(&destination, "existing").unwrap();
        import_msix_settings(root.path());
        assert_eq!(fs::read_to_string(&destination).unwrap(), "existing");

        let fresh = tempfile::tempdir().unwrap();
        import_msix_settings(fresh.path());
        assert!(!fresh.path().join("Mango").exists());
    }

    #[test]
    fn reads_legacy_apem_settings() {
        let root = tempfile::tempdir().unwrap();
        fs::create_dir_all(root.path().join("APEM")).unwrap();
        fs::write(root.path().join("APEM/settings.json"), r#"{"GsiToken":"legacy","IsTurboMode":true}"#).unwrap();
        let store = SettingsStore::load(root.path());
        assert_eq!(store.read().gsi_token, "legacy");
        assert!(store.read().is_turbo_mode);
    }
}
