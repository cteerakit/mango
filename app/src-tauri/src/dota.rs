//! Dota 2 install discovery, the GSI config file, and the HUD font (C# `GsiConfigInstaller`,
//! `HudFontService`).

use std::collections::HashSet;
use std::path::{Path, PathBuf};
use std::sync::LazyLock;

use regex::Regex;
use serde::Serialize;

use crate::settings::AppSettings;

const CONFIG_FILE: &str = "gamestate_integration_mango.cfg";
const LEGACY_CONFIG_FILE: &str = "gamestate_integration_apem.cfg";

#[derive(Debug, Clone, Default, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct GsiInstallResult {
    pub success: bool,
    pub message: String,
    pub paths: Vec<String>,
}

pub fn config_content(settings: &AppSettings) -> String {
    format!(
        r#""Mango"
{{
    "uri"               "http://127.0.0.1:{port}/"
    "timeout"           "5.0"
    "buffer"            "1.0"
    "throttle"          "1.0"
    "heartbeat"         "30.0"
    "data"
    {{
        "provider"      "1"
        "map"           "1"
        "player"        "1"
        "allplayers"    "1"
        "hero"          "1"
        "abilities"     "1"
        "items"         "1"
        "buildings"     "1"
        "draft"         "1"
        "events"        "1"
    }}
    "auth"
    {{
        "token"         "{token}"
    }}
}}"#,
        port = settings.gsi_port,
        token = settings.gsi_token
    )
}

/// Writes the GSI config into every Dota install and removes the old APEM one.
pub fn install_config(settings: &AppSettings) -> GsiInstallResult {
    install_config_into(&find_install_paths(), settings)
}

pub fn install_config_into(dota_paths: &[PathBuf], settings: &AppSettings) -> GsiInstallResult {
    if dota_paths.is_empty() {
        return GsiInstallResult {
            success: false,
            message: "Could not find Dota 2 installation. Install Dota via Steam first.".into(),
            paths: Vec::new(),
        };
    }
    let mut written = Vec::new();
    for dota in dota_paths {
        let dir = dota.join("game").join("dota").join("cfg").join("gamestate_integration");
        let path = dir.join(CONFIG_FILE);
        if std::fs::create_dir_all(&dir).and_then(|_| std::fs::write(&path, config_content(settings))).is_ok() {
            written.push(path.display().to_string());
        }
        let legacy = dir.join(LEGACY_CONFIG_FILE);
        if legacy.exists() {
            let _ = std::fs::remove_file(legacy);
        }
    }
    if written.is_empty() {
        return GsiInstallResult {
            success: false,
            message: "Found Dota 2, but could not write the GSI config. Check folder permissions.".into(),
            paths: written,
        };
    }
    GsiInstallResult {
        success: true,
        message: format!("Installed GSI config to {} location(s).", written.len()),
        paths: written,
    }
}

/// Every `steamapps/common/dota 2 beta` across Steam libraries.
pub fn find_install_paths() -> Vec<PathBuf> {
    let mut seen = HashSet::new();
    let mut paths = Vec::new();
    let mut add = |path: PathBuf| {
        if path.is_dir() && seen.insert(path.display().to_string().to_lowercase()) {
            paths.push(path);
        }
    };
    for root in steam_roots() {
        add(root.join("steamapps").join("common").join("dota 2 beta"));
        let library_file = root.join("steamapps").join("libraryfolders.vdf");
        if let Ok(content) = std::fs::read_to_string(&library_file) {
            for library in parse_library_folders(&content) {
                add(library.join("steamapps").join("common").join("dota 2 beta"));
            }
        }
    }
    paths
}

static LIBRARY_PATH: LazyLock<Regex> = LazyLock::new(|| Regex::new(r#""path"\s+"([^"]+)""#).expect("valid regex"));

pub fn parse_library_folders(vdf: &str) -> Vec<PathBuf> {
    LIBRARY_PATH.captures_iter(vdf).map(|c| PathBuf::from(c[1].replace(r"\\", r"\"))).collect()
}

fn steam_roots() -> Vec<PathBuf> {
    let mut roots = Vec::new();
    #[cfg(windows)]
    {
        use winreg::enums::HKEY_CURRENT_USER;
        if let Ok(key) = winreg::RegKey::predef(HKEY_CURRENT_USER).open_subkey(r"Software\Valve\Steam") {
            if let Ok(path) = key.get_value::<String, _>("SteamPath") {
                if !path.trim().is_empty() {
                    roots.push(PathBuf::from(path));
                }
            }
        }
    }
    for fallback in [r"C:\Program Files (x86)\Steam", r"C:\Program Files\Steam"] {
        let path = PathBuf::from(fallback);
        if path.is_dir() {
            roots.push(path);
        }
    }
    roots
}

/// Dota's own Radiance HUD font, so overlay text matches the in-game panels. `(regular, semibold)`.
pub fn hud_font_files() -> Option<(PathBuf, PathBuf)> {
    find_install_paths().into_iter().find_map(|dota| {
        let fonts = dota.join("game").join("dota").join("panorama").join("fonts");
        let regular = fonts.join("radiance-regular.otf");
        let semibold = fonts.join("radiance-semibold.otf");
        (regular.exists() && semibold.exists()).then_some((regular, semibold))
    })
}

pub fn console_log(dota: &Path) -> PathBuf {
    dota.join("game").join("dota").join("console.log")
}

pub fn steam_api_library(dota: &Path) -> PathBuf {
    dota.join("game").join("bin").join("win64").join("steam_api64.dll")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_library_folders() {
        let vdf = r#""libraryfolders" { "0" { "path" "C:\\Program Files (x86)\\Steam" } "1" { "path"		"D:\\SteamLibrary" } }"#;
        assert_eq!(
            parse_library_folders(vdf),
            [PathBuf::from(r"C:\Program Files (x86)\Steam"), PathBuf::from(r"D:\SteamLibrary")]
        );
    }

    #[test]
    fn installs_config_and_removes_legacy() {
        let dota = tempfile::tempdir().unwrap();
        let cfg_dir = dota.path().join("game/dota/cfg/gamestate_integration");
        std::fs::create_dir_all(&cfg_dir).unwrap();
        std::fs::write(cfg_dir.join(LEGACY_CONFIG_FILE), "old").unwrap();
        let settings = AppSettings { gsi_port: 40001, gsi_token: "secret".into(), ..Default::default() };

        let result = install_config_into(&[dota.path().to_path_buf()], &settings);

        assert!(result.success, "{}", result.message);
        let content = std::fs::read_to_string(cfg_dir.join(CONFIG_FILE)).unwrap();
        assert!(content.contains(r#""uri"               "http://127.0.0.1:40001/""#));
        assert!(content.contains(r#""token"         "secret""#));
        assert!(content.contains(r#""allplayers"    "1""#));
        assert!(!cfg_dir.join(LEGACY_CONFIG_FILE).exists());
        assert!(!install_config_into(&[], &settings).success);
    }
}
