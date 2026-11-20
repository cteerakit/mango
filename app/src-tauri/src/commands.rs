//! Commands the UI calls. Thin: logic lives in the services and `mango-core`.

use std::sync::Arc;

use mango_core::assets;
use mango_core::notes::{self, NoteVote};
use mango_core::rank_estimate::format_rank_tier;
use mango_core::ranks::{self, PlayerRankOverride};
use mango_core::text::{cmp_ignore_case, eq_ignore_case, is_blank};
use mango_core::steam_id;
use serde::{Deserialize, Serialize};
use tauri::State;
use tauri_plugin_dialog::DialogExt;
use tauri_plugin_global_shortcut::GlobalShortcutExt;

use crate::app::{AppState, SettingsView};
use crate::dota::{self, GsiInstallResult};
use crate::hotkeys;
use crate::match_store::StoreState;
use crate::match_view::{local_steam_id, MatchPageView};
use crate::mock;
use crate::opendota::{load_analysis_outcomes, PlayerOverview};
use crate::overlay::Rect;
use crate::overlay_state::{self, OverlayView};
use crate::settings::HotkeyBinding;
use crate::stratz::StratzReplay;
use crate::updater::{self, UpdateCheck};

type S<'a> = State<'a, Arc<AppState>>;

// ---- App / status ------------------------------------------------------------------------

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AppInfo {
    version: String,
    gsi_status: String,
    gsi_install: GsiInstallResult,
    hotkey_status: String,
    uses_game_font: bool,
}

#[tauri::command]
pub fn app_info(state: S) -> AppInfo {
    AppInfo {
        version: updater::installed_version(),
        gsi_status: state.gsi_status.lock().unwrap().clone(),
        gsi_install: state.gsi_install.lock().unwrap().clone(),
        hotkey_status: state.hotkey_status.lock().unwrap().clone(),
        uses_game_font: dota::hud_font_files().is_some(),
    }
}

#[tauri::command]
pub fn reinstall_gsi_config(state: S) -> GsiInstallResult {
    state.install_gsi_config()
}

#[tauri::command]
pub fn store_state(state: S) -> StoreState {
    state.store.state()
}

#[tauri::command]
pub fn match_view(state: S) -> MatchPageView {
    state.match_view.current()
}

// ---- Settings ----------------------------------------------------------------------------

#[tauri::command]
pub fn get_settings(state: S) -> SettingsView {
    SettingsView::from(&*state.settings.read())
}

#[derive(Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct SettingsPatch {
    theme: Option<u8>,
    show_player_panel: Option<bool>,
    show_bounty_timer: Option<bool>,
    show_power_timer: Option<bool>,
    show_wisdom_timer: Option<bool>,
    show_lotus_timer: Option<bool>,
    show_build_panel: Option<bool>,
    overlay_opacity: Option<f64>,
    timer_sounds_enabled: Option<bool>,
    is_turbo_mode: Option<bool>,
    rune_timer_lead_seconds: Option<i32>,
    show_mock_data: Option<bool>,
    steam_api_key: Option<String>,
    stratz_api_token: Option<String>,
}

#[tauri::command]
pub fn update_settings(state: S, patch: SettingsPatch) -> SettingsView {
    let mock_changed = patch.show_mock_data.is_some_and(|m| m != state.settings.read().show_mock_data);
    let tokens_changed = patch.stratz_api_token.is_some() || patch.steam_api_key.is_some();
    state.settings.update(|s| {
        macro_rules! set {
            ($($field:ident),*) => { $( if let Some(v) = patch.$field.clone() { s.$field = v; } )* };
        }
        set!(theme, show_player_panel, show_bounty_timer, show_power_timer, show_wisdom_timer, show_lotus_timer,
             show_build_panel, overlay_opacity, timer_sounds_enabled, is_turbo_mode, rune_timer_lead_seconds,
             show_mock_data, steam_api_key, stratz_api_token);
    });
    if mock_changed {
        state.apply_mock_mode();
    }
    if tokens_changed {
        state.match_view.refresh();
    }
    state.emit_settings();
    state.emit_overlay();
    SettingsView::from(&*state.settings.read())
}

// ---- Notes -------------------------------------------------------------------------------

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct NoteRow {
    key: String,
    player_name: String,
    player_id: String,
    content: String,
    like_count: i32,
    dislike_count: i32,
    is_toxic: bool,
    updated_at: Option<String>,
}

#[tauri::command]
pub fn notes_list(state: S) -> Vec<NoteRow> {
    let settings = state.settings.read();
    let mut rows: Vec<NoteRow> = settings
        .player_notes
        .values()
        .filter(|n| n.has_saved_data())
        .map(|n| NoteRow {
            key: n.storage_key(),
            player_name: n.player_name.clone(),
            player_id: n.player_id.clone(),
            content: n.content.clone(),
            like_count: n.like_count,
            dislike_count: n.dislike_count,
            is_toxic: n.is_toxic,
            updated_at: n.updated_at.map(|t| t.to_rfc3339()),
        })
        .collect();
    let dash = |s: &str| if is_blank(s) { "—".to_string() } else { s.to_string() };
    rows.sort_by(|a, b| {
        cmp_ignore_case(&dash(&a.player_name), &dash(&b.player_name))
            .then_with(|| cmp_ignore_case(&dash(&a.player_id), &dash(&b.player_id)))
    });
    rows
}

#[tauri::command]
pub fn note_save(state: S, player_name: String, player_id: String, content: String) -> Result<(), String> {
    state
        .settings
        .try_update(|s| notes::upsert(&mut s.player_notes, &player_name, &player_id, &content))
        .map_err(|e| format!("Could not save the note: {e}"))?;
    state.emit_notes_changed();
    Ok(())
}

#[tauri::command]
pub fn note_delete(state: S, key: String) -> Result<(), String> {
    state
        .settings
        .try_update(|s| s.player_notes.remove(&key))
        .map_err(|e| format!("Could not delete the note: {e}"))?;
    state.emit_notes_changed();
    Ok(())
}

#[tauri::command]
pub fn note_vote(state: S, player_name: String, player_id: String, vote: String) -> Result<bool, String> {
    let vote = if vote == "like" { NoteVote::Like } else { NoteVote::Dislike };
    let match_id = state.store.state().page_snapshot.match_id;
    let changed = state
        .settings
        .try_update(|s| notes::vote(&mut s.player_notes, &player_name, &player_id, &match_id, vote))
        .map_err(|e| format!("Could not save the vote: {e}"))?;
    state.emit_notes_changed();
    Ok(changed)
}

#[tauri::command]
pub fn note_toggle_toxic(state: S, player_name: String, player_id: String) -> Result<bool, String> {
    let changed = state
        .settings
        .try_update(|s| notes::toggle_toxic(&mut s.player_notes, &player_name, &player_id))
        .map_err(|e| format!("Could not save the mark: {e}"))?;
    state.emit_notes_changed();
    Ok(changed)
}

#[tauri::command]
pub async fn notes_export(state: S<'_>) -> Result<Option<String>, String> {
    let (json, count) = {
        let settings = state.settings.read();
        let count = settings.player_notes.iter().filter(|(k, n)| !is_blank(k) && n.has_saved_data()).count();
        (notes::serialize(&settings.player_notes), count)
    };
    let picked = state
        .app
        .dialog()
        .file()
        .set_file_name("mango-player-notes.json")
        .add_filter("Player notes", &["json"])
        .blocking_save_file();
    let Some(path) = picked.and_then(|p| p.into_path().ok()) else { return Ok(None) };
    std::fs::write(&path, json).map_err(|e| format!("Could not export notes: {e}"))?;
    let name = path.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
    Ok(Some(format!("Exported {count} note(s) to {name}.")))
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ImportPreview {
    imported: usize,
    existing: usize,
}

/// Opens a file and parses it; `None` when cancelled. Apply with [`notes_import_apply`].
#[tauri::command]
pub async fn notes_import_pick(state: S<'_>) -> Result<Option<ImportPreview>, String> {
    let picked = state.app.dialog().file().add_filter("Player notes", &["json"]).blocking_pick_file();
    let Some(path) = picked.and_then(|p| p.into_path().ok()) else { return Ok(None) };
    let text = std::fs::read_to_string(&path).map_err(|e| e.to_string())?;
    let imported = notes::try_parse(&text)?;
    if imported.is_empty() {
        return Err("That file didn't contain any notes.".into());
    }
    let preview = ImportPreview { imported: imported.len(), existing: state.settings.read().player_notes.len() };
    *state.pending_import.lock().unwrap() = Some(imported);
    Ok(Some(preview))
}

#[tauri::command]
pub fn notes_import_apply(state: S, replace: bool) -> Result<String, String> {
    let imported = state.pending_import.lock().unwrap().take().ok_or("Choose a file to import first.")?;
    let result = state
        .settings
        .try_update(|s| notes::apply(&mut s.player_notes, &imported, replace))
        .map_err(|e| format!("Could not import notes: {e}"))?;
    state.emit_notes_changed();
    Ok(if replace {
        format!("Replaced notes with {} imported note(s).", result.total)
    } else {
        format!("Imported notes: {} added, {} updated.", result.added, result.updated)
    })
}

// ---- Ranks -------------------------------------------------------------------------------

#[derive(Serialize)]
pub struct RankOption {
    tier: i32,
    label: String,
}

#[tauri::command]
pub fn rank_options() -> Vec<RankOption> {
    ranks::tiers().map(|tier| RankOption { tier, label: format_rank_tier(Some(tier), None) }).collect()
}

/// Saves (`tier: Some`) or clears a manual rank for a private or unranked player.
#[tauri::command]
pub fn rank_set(state: S, player_id: String, player_name: String, tier: Option<i32>) -> Result<String, String> {
    let account = steam_id::to_account_id(&player_id).ok_or("This player has no account id.")?;
    state
        .settings
        .try_update(|s| match tier {
            Some(tier) => {
                s.player_ranks.insert(account.to_string(), PlayerRankOverride { player_id, player_name, rank_tier: tier });
            }
            None => {
                s.player_ranks.shift_remove(&account.to_string());
            }
        })
        .map_err(|_| "Could not save the rank. Please try again.".to_string())?;
    state.emit_notes_changed();
    Ok(if tier.is_some() { "Rank saved for this player." } else { "Rank override cleared." }.into())
}

// ---- Player page -------------------------------------------------------------------------

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PickerItem {
    steam_id: String,
    name: String,
}

#[tauri::command]
pub fn player_picker(state: S) -> Vec<PickerItem> {
    let store = state.store.state();
    let settings = state.settings.read().clone();
    let local = local_steam_id(&store, &settings);
    let snapshot = &store.snapshot;
    let mut items: Vec<PickerItem> = Vec::new();
    fn upsert(items: &mut Vec<PickerItem>, id: &str, name: &str) {
        let id = id.trim();
        if id.is_empty() {
            return;
        }
        match items.iter_mut().find(|i| eq_ignore_case(&i.steam_id, id)) {
            Some(existing) if is_blank(&existing.name) && !is_blank(name) => existing.name = name.trim().into(),
            Some(_) => {}
            None => items.push(PickerItem { steam_id: id.into(), name: name.trim().into() }),
        }
    }
    if !local.is_empty() {
        let name = [snapshot.player_name.as_str()]
            .into_iter()
            .chain(snapshot.players.iter().filter(|p| eq_ignore_case(&p.steam_id, &local)).map(|p| p.name.as_str()))
            .find(|n| !is_blank(n))
            .unwrap_or("You");
        upsert(&mut items, &local, name);
    }
    for note in settings.player_notes.values() {
        upsert(&mut items, &note.player_id, &note.player_name);
    }
    for player in &snapshot.players {
        upsert(&mut items, &player.steam_id, &player.name);
    }
    let known: Vec<String> = items.iter().filter_map(|i| steam_id::to_account_id(&i.steam_id)).map(|a| a.to_string()).collect();
    for (account, rank) in &settings.player_ranks {
        if !known.contains(account) {
            upsert(&mut items, account, &rank.player_name);
        }
    }
    items.sort_by(|a, b| {
        (!eq_ignore_case(&a.steam_id, &local))
            .cmp(&!eq_ignore_case(&b.steam_id, &local))
            .then_with(|| cmp_ignore_case(&a.name, &b.name))
            .then_with(|| cmp_ignore_case(&a.steam_id, &b.steam_id))
    });
    items
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct LocalIdentity {
    steam_id: String,
    name: String,
    note: String,
    likes: i32,
    dislikes: i32,
}

/// The local account id (GSI, else last seen); empty when never detected.
#[tauri::command]
pub fn local_player(state: S) -> String {
    let store = state.store.state();
    let id = if store.is_using_mock_data { store.snapshot.steam_id.clone() } else { local_steam_id(&store, &state.settings.read()) };
    if steam_id::to_account_id(&id).is_some() { id } else { String::new() }
}

#[tauri::command]
pub fn player_identity(state: S, steam_id: String) -> LocalIdentity {
    let settings = state.settings.read();
    let note = notes::get_by_player_id(&settings.player_notes, &steam_id);
    let snapshot = state.store.state().snapshot;
    let name = note
        .map(|n| n.player_name.clone())
        .filter(|n| !is_blank(n))
        .or_else(|| snapshot.players.iter().find(|p| eq_ignore_case(&p.steam_id, &steam_id)).map(|p| p.name.clone()))
        .unwrap_or_default();
    LocalIdentity {
        steam_id,
        name,
        note: note.map(|n| n.content.clone()).unwrap_or_default(),
        likes: note.map_or(0, |n| n.like_count),
        dislikes: note.map_or(0, |n| n.dislike_count),
    }
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PlayerProfile {
    overview: Option<PlayerOverview>,
    rank_medal: Option<ProfileRankMedal>,
    error: Option<String>,
}

#[derive(Serialize)]
struct ProfileRankMedal {
    icon: String,
    star: String,
    estimated: bool,
    label: String,
}

#[tauri::command]
pub async fn player_profile(state: S<'_>, steam_id: String) -> Result<PlayerProfile, String> {
    let is_mock = state.store.state().is_using_mock_data;
    let overview = if is_mock {
        Some(mock::player_overview(&steam_id))
    } else {
        let mut overview = state.opendota.player_overview(&steam_id).await;
        let token = state.settings.read().stratz_api_token.clone();
        if let Some(profile) = overview.as_mut().filter(|_| !token.trim().is_empty()) {
            let replay = StratzReplay { stratz: state.stratz.clone(), token };
            let ids = profile.recent_matches.iter().filter_map(|row| row.match_id.parse::<i64>().ok());
            let outcomes = load_analysis_outcomes(Some(&replay), ids).await;
            for row in &mut profile.recent_matches {
                row.analysis_outcome = row.match_id.parse::<i64>().ok().and_then(|id| outcomes.get(&id).copied());
            }
        }
        overview
    };
    let settings = state.settings.read();
    let (private, reported) = overview.as_ref().map_or((false, None), |o| (o.is_private, o.rank_tier));
    let saved = ranks::get(&settings.player_ranks, &steam_id, private, reported);
    let rank = if let Some(saved) = saved {
        let tier = Some(saved.rank_tier);
        Some((tier, false, format!("Manual: {}", format_rank_tier(tier, None))))
    } else {
        overview.as_ref().map(|o| (o.display_rank_tier, o.rank_is_estimated, o.rank_label.clone()))
    };
    let rank_medal = rank.map(|(tier, estimated, label)| ProfileRankMedal {
        icon: assets::rank_icon(tier),
        star: assets::rank_star(tier),
        estimated,
        label: if tier.is_none() && label == "—" { "Unranked".into() } else { label },
    });
    let error = overview.is_none().then(|| {
        if steam_id::to_account_id(&steam_id).is_none() {
            "This player ID is not a valid Steam or OpenDota account ID.".to_string()
        } else {
            "OpenDota is temporarily unavailable. Check your internet connection and tap Retry, or try again in a few minutes.".to_string()
        }
    });
    Ok(PlayerProfile { overview, rank_medal, error })
}

// ---- Overlay -----------------------------------------------------------------------------

// Async so window creation runs off the event-loop thread (sync commands there deadlock on Windows).
#[tauri::command]
pub async fn overlay_set_visible(state: S<'_>, visible: bool) -> Result<(), String> {
    state.set_overlay_visible(visible);
    Ok(())
}

#[tauri::command]
pub async fn overlay_set_interactive(state: S<'_>, interactive: bool) -> Result<(), String> {
    state.set_interactive(interactive);
    Ok(())
}

#[tauri::command]
pub fn overlay_state(state: S) -> OverlayView {
    state.overlay_view()
}

#[tauri::command]
pub fn overlay_set_regions(state: S, rects: Vec<Rect>) {
    state.overlay.set_regions(&state.app, rects);
}

#[tauri::command]
pub fn overlay_save_position(state: S, widget: String, x: f64, y: f64) {
    state.settings.update(|s| overlay_state::set_position(&mut s.panel_layout, &widget, x, y));
}

#[tauri::command]
pub fn overlay_reset_positions(state: S) {
    state.settings.update(|s| s.panel_layout = Default::default());
    state.emit_overlay();
}

/// The Radiance HUD font from the local Dota install (`semibold` or `regular`), as raw bytes.
#[tauri::command]
pub fn hud_font(weight: String) -> Result<tauri::ipc::Response, String> {
    let (regular, semibold) = dota::hud_font_files().ok_or("Dota 2 fonts not found")?;
    let path = if weight == "semibold" { semibold } else { regular };
    std::fs::read(path).map(tauri::ipc::Response::new).map_err(|e| e.to_string())
}

// ---- Hotkeys -----------------------------------------------------------------------------

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RecordedKey {
    ctrl: bool,
    alt: bool,
    shift: bool,
    win: bool,
    /// DOM `KeyboardEvent.code`, e.g. `F9`.
    code: Option<String>,
    virtual_key: Option<u32>,
}

impl RecordedKey {
    fn binding(&self) -> Option<HotkeyBinding> {
        let vk = self.virtual_key.or_else(|| self.code.as_deref().and_then(hotkeys::virtual_key_for_dom_code))?;
        Some(HotkeyBinding { ctrl: self.ctrl, alt: self.alt, shift: self.shift, win: self.win, virtual_key: vk })
    }
}

/// Display text for a recorded key, or why it cannot be used.
#[tauri::command]
pub fn hotkey_describe(key: RecordedKey) -> Result<String, String> {
    let binding = key.binding().ok_or("That key can't be used for a shortcut.")?;
    Ok(hotkeys::display(&binding))
}

#[tauri::command]
pub fn hotkeys_save(state: S, toggle_overlay: RecordedKey, toggle_interactive: RecordedKey) -> Result<String, String> {
    let overlay = toggle_overlay.binding().ok_or("Choose a key for Toggle overlay.")?;
    let interactive = toggle_interactive.binding().ok_or("Choose a key for Interactive mode.")?;
    if overlay == interactive {
        return Err("Overlay and interactive shortcuts must be different.".into());
    }
    if !overlay.has_modifier() || !interactive.has_modifier() {
        return Err("Each shortcut needs at least one modifier (Ctrl, Alt, Shift, or Win).".into());
    }
    state.settings.update(|s| {
        s.toggle_overlay_hotkey = overlay;
        s.toggle_interactive_hotkey = interactive;
    });
    let applied = register_hotkeys(&state);
    state.emit_settings();
    Ok(if applied {
        "Shortcuts saved and applied.".into()
    } else {
        "Saved, but Windows rejected one or both hotkeys (they may already be in use).".into()
    })
}

/// (Re)registers both hotkeys from settings. Returns whether both succeeded.
pub fn register_hotkeys(state: &AppState) -> bool {
    let shortcuts = state.app.global_shortcut();
    let _ = shortcuts.unregister_all();
    let settings = state.settings.read().clone();
    let mut ok = true;
    let mut failures = Vec::new();
    for (binding, name) in [(settings.toggle_overlay_hotkey, "toggle overlay"), (settings.toggle_interactive_hotkey, "interactive mode")] {
        match hotkeys::shortcut_for(&binding) {
            Some(shortcut) if shortcuts.register(shortcut).is_ok() => {}
            _ => {
                ok = false;
                failures.push(format!("{} ({name})", hotkeys::display(&binding)));
            }
        }
    }
    if !ok {
        crate::log!("hotkeys rejected: {}", failures.join(", "));
    }
    *state.hotkey_status.lock().unwrap() = if ok {
        String::new()
    } else {
        format!("Windows rejected {}; another app may be using it.", failures.join(" and "))
    };
    ok
}

// ---- Updates -----------------------------------------------------------------------------

#[tauri::command]
pub async fn update_check(state: S<'_>) -> Result<UpdateCheck, String> {
    let result = updater::check(&state.http, &updater::installed_version()).await;
    *state.available_update.lock().unwrap() = Some(result.clone());
    Ok(result)
}

/// Downloads, verifies and starts the installer, then exits so it can replace files.
#[tauri::command]
pub async fn update_install(state: S<'_>) -> Result<String, String> {
    let update = state.available_update.lock().unwrap().clone().ok_or("No update is available to install.")?;
    let installer = updater::download(&state.http, &update, &std::env::temp_dir(), |_| {}).await?;
    if updater::start_installer(&installer) {
        state.app.exit(0);
        Ok("Installing update. Mango will close and reopen.".into())
    } else {
        Err("Could not start the installer. Use Download installer manually instead.".into())
    }
}
