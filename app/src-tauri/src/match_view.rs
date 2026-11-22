//! What the Match page shows (C# `MatchPage` code-behind, moved out of the UI).
//!
//! Decorates the page snapshot's players with notes, votes, saved ranks, OpenDota profile data
//! and match history, loading the last two in the background. The UI only renders the result.

use std::collections::HashMap;
use std::sync::{Arc, Mutex};

use futures::stream::{self, StreamExt};
use mango_core::history::PlayerMatchHistory;
use mango_core::model::MatchPlayer;
use mango_core::rank_estimate::{self, format_rank_tier};
use mango_core::{assets, match_id, notes, ranks, steam_id};
use serde::Serialize;

use crate::match_store::{MatchStore, StoreState};
use crate::mock;
use crate::opendota::{OpenDota, ReplaySource};
use crate::settings::{AppSettings, SettingsStore};
use crate::stratz::{Stratz, StratzReplay};

#[derive(Debug, Clone, Default, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PlayerRow {
    #[serde(flatten)]
    pub player: MatchPlayer,
    pub is_party_member: bool,
    pub hero_key: String,
    pub display_hero_name: String,
    pub can_edit_rank: bool,
    pub can_mark_toxic: bool,
    pub position_label: String,
    pub result_kda: String,
}

#[derive(Debug, Clone, Default, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct MatchPageView {
    pub match_id: String,
    pub subtitle: String,
    pub is_previous_match: bool,
    pub is_mock: bool,
    pub players: Vec<PlayerRow>,
}

#[derive(Debug, Clone, Default)]
struct Enrichment {
    avatar_url: String,
    rank_icon_url: String,
    rank_star_url: String,
    rank_label: String,
    rank_is_estimated: bool,
    matches_label: String,
    possible_smurf: bool,
    is_profile_private: bool,
    reported_rank_tier: Option<i32>,
}

#[derive(Default)]
struct Inner {
    party: Vec<MatchPlayer>,
    enrichment: HashMap<u64, Enrichment>,
    history: HashMap<String, PlayerMatchHistory>,
    enrichment_accounts: Vec<u64>,
    history_key: String,
    enrichment_task: Option<tauri::async_runtime::JoinHandle<()>>,
    history_task: Option<tauri::async_runtime::JoinHandle<()>>,
    last: Option<MatchPageView>,
}

pub struct MatchView {
    store: Arc<MatchStore>,
    settings: Arc<SettingsStore>,
    opendota: Arc<OpenDota>,
    stratz: Arc<Stratz>,
    inner: Mutex<Inner>,
    on_change: Box<dyn Fn(&MatchPageView) + Send + Sync>,
}

impl MatchView {
    pub fn new(
        store: Arc<MatchStore>,
        settings: Arc<SettingsStore>,
        opendota: Arc<OpenDota>,
        stratz: Arc<Stratz>,
        on_change: impl Fn(&MatchPageView) + Send + Sync + 'static,
    ) -> Arc<Self> {
        Arc::new(Self { store, settings, opendota, stratz, inner: Mutex::new(Inner::default()), on_change: Box::new(on_change) })
    }

    fn lock(&self) -> std::sync::MutexGuard<'_, Inner> {
        self.inner.lock().unwrap_or_else(|p| p.into_inner())
    }

    pub fn set_party(self: &Arc<Self>, party: Vec<MatchPlayer>) {
        let mut inner = self.lock();
        if inner.party == party { return; }
        inner.party = party;
        drop(inner);
        self.refresh();
    }

    fn display_state(&self) -> StoreState {
        let mut state = self.store.state();
        apply_party_fallback(&mut state, &self.lock().party);
        state
    }

    pub fn current(&self) -> MatchPageView {
        let state = self.display_state();
        let settings = self.settings.read().clone();
        let inner = self.lock();
        build_view(&state, &settings, &inner.enrichment, &inner.history, &inner.party)
    }

    /// Rebuilds the view, emits it when it changed, and starts any background loads it needs.
    pub fn refresh(self: &Arc<Self>) {
        let state = self.display_state();
        let settings = self.settings.read().clone();
        let view = {
            let mut inner = self.lock();
            let view = build_view(&state, &settings, &inner.enrichment, &inner.history, &inner.party);
            if inner.last.as_ref() == Some(&view) {
                None
            } else {
                inner.last = Some(view.clone());
                Some(view)
            }
        };
        if let Some(view) = &view {
            (self.on_change)(view);
        }
        if !state.is_using_mock_data {
            self.queue_enrichment(&state.page_snapshot.players);
            self.queue_history(&state, &settings);
        }
    }

    fn queue_enrichment(self: &Arc<Self>, players: &[MatchPlayer]) {
        let mut accounts: Vec<u64> = players
            .iter()
            .filter_map(|p| steam_id::to_account_id(&p.steam_id))
            .collect();
        accounts.sort_unstable();
        accounts.dedup();
        let mut inner = self.lock();
        if inner.enrichment_accounts == accounts {
            return;
        }
        inner.enrichment_accounts = accounts.clone();
        if let Some(task) = inner.enrichment_task.take() {
            task.abort();
        }
        if accounts.is_empty() {
            return;
        }
        let view = Arc::clone(self);
        inner.enrichment_task = Some(tauri::async_runtime::spawn(async move {
            let overviews = futures::future::join_all(accounts.iter().map(|account| async {
                view.opendota.player_overview(&account.to_string()).await
            })).await;
            let lobby_average = rank_estimate::average_rank_tier(
                overviews.iter().flatten().filter_map(|o| o.rank_tier).filter(|t| *t > 0),
            );
            {
                let mut inner = view.lock();
                if inner.enrichment_accounts != accounts {
                    return;
                }
                for (account, overview) in accounts.into_iter().zip(overviews) {
                    let private = overview.as_ref().is_some_and(|o| o.is_private);
                    let resolved = rank_estimate::resolve(
                        overview.as_ref().and_then(|o| o.rank_tier),
                        overview.as_ref().and_then(|o| o.leaderboard_rank),
                        overview.as_ref().and_then(|o| o.history_rank_estimate),
                        overview.as_ref().and_then(|o| o.mmr_estimate),
                        lobby_average,
                        private,
                    );
                    let has_rank = resolved.rank_tier.is_some_and(|t| t > 0);
                    inner.enrichment.insert(
                        account,
                        Enrichment {
                            avatar_url: overview.as_ref().map(|o| o.avatar_url.clone()).unwrap_or_default(),
                            rank_icon_url: if has_rank { assets::rank_icon(resolved.rank_tier) } else { String::new() },
                            rank_star_url: if has_rank { assets::rank_star(resolved.rank_tier) } else { String::new() },
                            rank_label: resolved.label,
                            rank_is_estimated: resolved.is_estimated,
                            matches_label: match &overview {
                                _ if private => "—".into(),
                                None => String::new(),
                                Some(o) => {
                                    let total = rank_estimate::thousands(o.wins + o.losses);
                                    match o.win_rate_percent {
                                        Some(rate) => format!("{total} ({})", trim_rate(rate)),
                                        None => total,
                                    }
                                }
                            },
                            possible_smurf: overview.as_ref().is_some_and(|o| o.possible_smurf),
                            is_profile_private: private,
                            reported_rank_tier: overview.as_ref().and_then(|o| o.rank_tier),
                        },
                    );
                }
                inner.enrichment_task = None;
            }
            view.refresh();
        }));
    }

    fn queue_history(self: &Arc<Self>, state: &StoreState, settings: &AppSettings) {
        let snapshot = &state.page_snapshot;
        let local = local_steam_id(state, settings);
        let requests: Vec<(String, String, String)> = snapshot
            .players
            .iter()
            .map(|p| (history_key(&local, &snapshot.match_id, p), p.steam_id.clone(), p.hero_icon_key()))
            .collect();
        let key = requests.iter().map(|r| r.0.as_str()).collect::<Vec<_>>().join("\u{1e}");
        let mut inner = self.lock();
        if inner.history_key == key {
            return;
        }
        inner.history_key = key.clone();
        if let Some(task) = inner.history_task.take() {
            task.abort();
        }
        let pending: Vec<_> = requests.into_iter().filter(|r| !inner.history.contains_key(&r.0)).collect();
        if pending.is_empty() {
            return;
        }
        let token = settings.stratz_api_token.clone();
        let match_id = snapshot.match_id.clone();
        let view = Arc::clone(self);
        inner.history_task = Some(tauri::async_runtime::spawn(async move {
            let replay = (!token.is_empty()).then(|| StratzReplay { stratz: view.stratz.clone(), token });
            let results = load_history(&view.opendota, pending, &local, &match_id, replay.as_ref().map(|r| r as &dyn ReplaySource)).await;
            {
                let mut inner = view.lock();
                if inner.history_key != key {
                    return;
                }
                inner.history.extend(results);
                inner.history_task = None;
            }
            view.refresh();
        }));
    }
}

/// Loads recent matches, encounters and the latest hero build for each player, three at a time
/// (C# `MatchHistoryLoader`). A failing lookup leaves its own fallback text.
pub async fn load_history(
    opendota: &OpenDota,
    requests: Vec<(String, String, String)>,
    local_steam_id: &str,
    match_id: &str,
    replay: Option<&dyn ReplaySource>,
) -> HashMap<String, PlayerMatchHistory> {
    stream::iter(requests)
        .map(|(key, steam, hero)| async move {
            let ((recent, recent_label), (recent_encounters, encounter_label), build) = tokio::join!(
                opendota.recent_match_summaries(&steam, match_id, replay),
                opendota.encounters(local_steam_id, &steam, match_id),
                opendota.latest_hero_build(&steam, &hero, match_id, replay),
            );
            let history = PlayerMatchHistory {
                encounter_label,
                recent_encounters,
                recent_matches: recent,
                recent_matches_label: recent_label,
                ..build
            };
            (key, history)
        })
        .buffer_unordered(3)
        .collect()
        .await
}

fn trim_rate(rate: f64) -> String {
    let text = format!("{rate:.1}");
    format!("{}%", text.strip_suffix(".0").unwrap_or(&text))
}

/// The user's account: from GSI when known, else the last one seen.
pub fn local_steam_id(state: &StoreState, settings: &AppSettings) -> String {
    let from_gsi = &state.page_snapshot.steam_id;
    if steam_id::to_account_id(from_gsi).is_some() {
        from_gsi.clone()
    } else {
        settings.steam_id.clone().unwrap_or_default()
    }
}

fn history_key(local: &str, match_id: &str, player: &MatchPlayer) -> String {
    format!("{local}|{match_id}|{}|{}", player.steam_id, player.hero_icon_key())
}

fn apply_party_fallback(state: &mut StoreState, party: &[MatchPlayer]) {
    if state.is_using_mock_data || state.is_showing_previous_match || state.snapshot.is_in_match() {
        return;
    }
    if let Some(local) = party.first() {
        state.page_snapshot = Default::default();
        state.page_snapshot.steam_id = local.steam_id.clone();
        state.page_snapshot.players = party.to_vec();
        state.roster_source = "steam-party".into();
    }
}

fn build_view(
    state: &StoreState,
    settings: &AppSettings,
    enrichment: &HashMap<u64, Enrichment>,
    history: &HashMap<String, PlayerMatchHistory>,
    party: &[MatchPlayer],
) -> MatchPageView {
    let snapshot = &state.page_snapshot;
    let local = local_steam_id(state, settings);
    let local_account = steam_id::to_account_id(&local);
    let players: Vec<PlayerRow> = snapshot
        .players
        .iter()
        .map(|source| {
            let mut player = source.clone();
            let note = notes::get_by_player_id(&settings.player_notes, &player.steam_id);
            player.note = note.map(|n| n.content.clone()).unwrap_or_default();
            player.like_count = note.map_or(0, |n| n.like_count);
            player.dislike_count = note.map_or(0, |n| n.dislike_count);
            player.is_toxic = note.is_some_and(|n| n.is_toxic);
            let (can_vote, reason) = vote_availability(&player, &snapshot.match_id);
            player.can_vote = can_vote;
            player.vote_unavailable_reason = reason.into();
            player.current_match_vote = note
                .filter(|n| n.has_voted_in_match(&snapshot.match_id))
                .map(|n| n.last_vote.clone())
                .unwrap_or_default();

            let account = steam_id::to_account_id(&player.steam_id);
            let e = account.and_then(|id| enrichment.get(&id)).cloned().unwrap_or_default();
            player.avatar_url = e.avatar_url;
            player.rank_icon_url = e.rank_icon_url;
            player.rank_star_url = e.rank_star_url;
            player.rank_label = e.rank_label;
            player.rank_is_estimated = e.rank_is_estimated;
            player.matches_label = e.matches_label;
            player.possible_smurf = e.possible_smurf;
            player.is_profile_private = e.is_profile_private;
            player.reported_rank_tier = e.reported_rank_tier;
            if player.is_profile_private && player.rank_icon_url.is_empty() {
                player.rank_icon_url = assets::rank_icon(None);
                player.rank_label = "Private profile · Rank unavailable".into();
            }

            if let Some(saved) =
                ranks::get(&settings.player_ranks, &player.steam_id, player.is_profile_private, player.reported_rank_tier)
            {
                player.rank_icon_url = assets::rank_icon(Some(saved.rank_tier));
                player.rank_star_url = assets::rank_star(Some(saved.rank_tier));
                player.rank_label = format!("Manual: {}", format_rank_tier(Some(saved.rank_tier), None));
                player.rank_is_estimated = false;
            }

            player.is_local_player =
                local_account.is_some() && local_account == account;
            player.hero_image_url = assets::hero_icon(&player.hero_icon_key());
            player.history = history.get(&history_key(&local, &snapshot.match_id, &player)).cloned().unwrap_or_default();
            if state.is_using_mock_data {
                mock::apply_preview_details(&mut player);
            }
            PlayerRow {
                is_party_member: !state.is_using_mock_data && account.is_some_and(|id| party.iter().any(|member| steam_id::to_account_id(&member.steam_id) == Some(id))),
                hero_key: player.hero_icon_key(),
                display_hero_name: player.display_hero_name(),
                can_edit_rank: player.can_edit_rank(),
                can_mark_toxic: player.can_mark_toxic(),
                position_label: player.history.position_label(),
                result_kda: player.history.result_kda(),
                player,
            }
        })
        .collect();

    MatchPageView {
        match_id: snapshot.match_id.clone(),
        subtitle: describe_roster(state, players.len()),
        is_previous_match: state.is_showing_previous_match,
        is_mock: state.is_using_mock_data,
        players,
    }
}

fn vote_availability(player: &MatchPlayer, match_id_text: &str) -> (bool, &'static str) {
    if player.steam_id.trim().is_empty() {
        (false, "Player has no ID.")
    } else if !match_id::is_usable(match_id_text) {
        (false, "Waiting for a match ID.")
    } else {
        (true, "")
    }
}

fn describe_roster(state: &StoreState, player_count: usize) -> String {
    let snapshot = &state.page_snapshot;
    if state.is_using_mock_data {
        return "Sample roster — mock data is enabled.".into();
    }
    if state.roster_source == "steam-party" { return "Your Dota party".into(); }
    if state.is_showing_previous_match {
        return "Last match — stays here until the next match starts.".into();
    }
    if !snapshot.is_connected {
        return "Waiting for Dota 2.".into();
    }
    match player_count {
        0 => "Connected, but this GSI payload has no player roster yet.".into(),
        1 => {
            if !state.roster_fetch_message.is_empty() {
                state.roster_fetch_message.clone()
            } else if state.roster_fetch_failed {
                if match_id::is_usable(&snapshot.match_id) {
                    "Only your player is in GSI. Could not load the full lobby yet — retrying automatically.".into()
                } else {
                    "Only your local player is in GSI. Waiting for a match ID, then the lobby will load from Steam/OpenDota.".into()
                }
            } else if match_id::is_usable(&snapshot.match_id) {
                "Only your player is in GSI so far. Loading the full lobby from Steam…".into()
            } else if snapshot.is_in_draft() {
                "While playing, Dota GSI usually only sends your own player during pick. Waiting for a match ID to load the lobby.".into()
            } else {
                "Only your local player is in this GSI payload. Loading the full lobby from Steam…".into()
            }
        }
        n if snapshot.players.iter().any(|p| p.team_name.trim().is_empty()) => {
            format!("Showing {n} players. Team and hero details are not available for every player yet.")
        }
        _ => "KDA, position and items are from each player's latest completed match on their hero. History loads in the background.".into(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use mango_core::model::MatchSnapshot;
    use mango_core::notes::{NoteVote, NotesMap};

    fn state(players: Vec<MatchPlayer>, match_id: &str) -> StoreState {
        let snapshot = MatchSnapshot {
            is_connected: true,
            match_id: match_id.into(),
            steam_id: "1".into(),
            game_state: "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS".into(),
            players,
            ..Default::default()
        };
        StoreState {
            page_snapshot: snapshot.clone(),
            snapshot,
            is_showing_previous_match: false,
            is_gsi_connected: true,
            is_using_mock_data: false,
            connection_status: String::new(),
            last_payload_utc: None,
            roster_fetch_failed: false,
            roster_fetch_message: String::new(),
            roster_source: String::new(),
        }
    }

    fn p(id: &str, team: &str) -> MatchPlayer {
        MatchPlayer { steam_id: id.into(), name: format!("P{id}"), team_name: team.into(), hero_name: "npc_dota_hero_axe".into(), ..Default::default() }
    }

    #[test]
    fn decorates_players_with_notes_votes_and_saved_ranks() {
        let mut settings = AppSettings::default();
        let mut notes = NotesMap::new();
        notes::upsert(&mut notes, "P2", "2", "Feeds");
        notes::vote(&mut notes, "P2", "2", "100", NoteVote::Dislike);
        settings.player_notes = notes;
        settings.player_ranks.insert("2".into(), ranks::PlayerRankOverride { rank_tier: 54, ..Default::default() });

        let view = build_view(&state(vec![p("1", "radiant"), p("2", "dire")], "100"), &settings, &HashMap::new(), &HashMap::new(), &[]);

        let me = &view.players[0];
        assert!(me.player.is_local_player);
        assert!(me.hero_key == "axe" && me.player.hero_image_url.ends_with("/heroes/axe.png"));
        let other = &view.players[1].player;
        assert_eq!(other.note, "Feeds");
        assert_eq!(other.dislike_count, 1);
        assert_eq!(other.current_match_vote, "dislike");
        assert!(other.can_vote);
        assert_eq!(other.rank_label, "Manual: Legend 4");
        assert!(view.subtitle.starts_with("KDA, position and items"));
    }

    #[test]
    fn votes_need_an_id_and_a_match_id() {
        let view = build_view(&state(vec![p("", "radiant"), p("2", "dire")], "0"), &AppSettings::default(), &HashMap::new(), &HashMap::new(), &[]);
        assert_eq!(view.players[0].player.vote_unavailable_reason, "Player has no ID.");
        assert_eq!(view.players[1].player.vote_unavailable_reason, "Waiting for a match ID.");
    }

    #[test]
    fn private_profile_without_rank_has_a_placeholder_medal() {
        let state = state(vec![p("2", "dire")], "100");
        let enrichment = HashMap::from([(2, Enrichment {
            is_profile_private: true,
            ..Default::default()
        })]);
        let view = build_view(&state, &AppSettings::default(), &enrichment, &HashMap::new(), &[]);

        assert_eq!(view.players[0].player.rank_icon_url, assets::rank_icon(None));
        assert_eq!(view.players[0].player.rank_label, "Private profile · Rank unavailable");
    }

    #[test]
    fn party_fallback_preserves_the_finished_match() {
        let mut state = state(vec![p("1", "radiant"), p("2", "dire")], "100");
        state.snapshot = MatchSnapshot::default();
        state.is_showing_previous_match = true;
        let party = [p("1", ""), p("3", "")];
        let finished = state.page_snapshot.clone();

        apply_party_fallback(&mut state, &party);

        assert_eq!(state.page_snapshot, finished);
        assert!(state.is_showing_previous_match);

        state.is_showing_previous_match = false;
        state.page_snapshot = MatchSnapshot::default();
        apply_party_fallback(&mut state, &party);

        assert_eq!(state.page_snapshot.players, party);
        assert_eq!(state.roster_source, "steam-party");
    }

    #[test]
    fn subtitles_explain_sparse_rosters() {
        let mut s = state(vec![p("1", "radiant")], "100");
        let settings = AppSettings::default();
        assert!(build_view(&s, &settings, &HashMap::new(), &HashMap::new(), &[]).subtitle.contains("Loading the full lobby"));
        s.roster_fetch_message = "Steam said no.".into();
        assert_eq!(build_view(&s, &settings, &HashMap::new(), &HashMap::new(), &[]).subtitle, "Steam said no.");
        s.is_showing_previous_match = true;
        assert!(build_view(&s, &settings, &HashMap::new(), &HashMap::new(), &[]).subtitle.starts_with("Last match"));
        let teamless = state(vec![p("1", ""), p("2", "")], "100");
        assert!(build_view(&teamless, &settings, &HashMap::new(), &HashMap::new(), &[]).subtitle.starts_with("Showing 2 players"));
    }

    #[test]
    fn mock_mode_fills_preview_details() {
        let mut s = state(mock::players(), "debug-x");
        s.is_using_mock_data = true;
        let view = build_view(&s, &AppSettings::default(), &HashMap::new(), &HashMap::new(), &[]);
        assert!(view.is_mock);
        assert_eq!(view.players[1].player.matches_label, "2,964 (50.1%)");
        assert!(!view.players[1].player.history.items.is_empty());
        let encounters = &view.players[1].player.history.recent_encounters;
        assert_eq!(encounters.len(), 5);
        assert_eq!(encounters[0].side, mango_core::history::EncounterSide::With);
        assert_eq!(encounters[1].side, mango_core::history::EncounterSide::Against);
        assert_eq!(encounters[0].summary.result, "Win");
        assert_eq!(encounters[1].summary.result, "Loss");
        assert_ne!(encounters[0].summary.hero_image_url, encounters[1].summary.hero_image_url);
        let smurf = view.players.iter().find(|row| row.player.possible_smurf).unwrap();
        assert_eq!(smurf.player.matches_label, "180 (70%)");
        assert!(!smurf.player.steam_id.is_empty());
        let profile = mock::player_overview(&smurf.player.steam_id);
        assert!(profile.possible_smurf);
        assert_eq!(profile.display_name, smurf.player.name);
        assert_eq!(profile.wins + profile.losses, 180);
        assert_eq!(profile.win_rate_percent, Some(70.0));
    }

    #[test]
    fn win_rates_drop_a_trailing_zero() {
        assert_eq!(trim_rate(48.9), "48.9%");
        assert_eq!(trim_rate(50.0), "50%");
    }
}
