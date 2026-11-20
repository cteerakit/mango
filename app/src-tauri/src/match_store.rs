//! Live match state (C# `MatchStore`).
//!
//! Turns GSI payloads into snapshots, keeps a fetched lobby while sparse GSI updates arrive,
//! looks up the lobby from external sources when GSI lacks it, and tracks what the Match page
//! shows after a game ends.

use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use chrono::{DateTime, Utc};
use mango_core::gsi::{self, GsiPayload};
use mango_core::model::MatchSnapshot;
use mango_core::page_state::MatchPageState;
use mango_core::{match_id, roster, steam_id};
use serde::Serialize;

use crate::roster_sources::{RosterResolution, RosterResolver};

const MIN_PUBLISH_INTERVAL: Duration = Duration::from_secs(1);
const MIN_ROSTER_FETCH_INTERVAL: Duration = Duration::from_secs(15);
const FAILED_ROSTER_FETCH_INTERVAL: Duration = Duration::from_secs(5);
const STRATEGY_ROSTER_FETCH_INTERVAL: Duration = Duration::from_secs(1);
const ROSTER_FETCH_TIMEOUT: Duration = Duration::from_secs(25);
/// GSI sends a heartbeat every 30 s while Dota runs; silence this long means it closed.
pub const STALE_AFTER: Duration = Duration::from_secs(75);
const WAITING: &str = "Waiting for Dota 2";

/// What the UI needs about the live connection.
#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct StoreState {
    pub snapshot: MatchSnapshot,
    /// The match the Match page shows (the last one, after a game ends).
    pub page_snapshot: MatchSnapshot,
    pub is_showing_previous_match: bool,
    pub is_gsi_connected: bool,
    pub is_using_mock_data: bool,
    pub connection_status: String,
    pub last_payload_utc: Option<DateTime<Utc>>,
    pub roster_fetch_failed: bool,
    pub roster_fetch_message: String,
    pub roster_source: String,
}

struct Inner {
    snapshot: MatchSnapshot,
    page: MatchPageState,
    is_gsi_connected: bool,
    is_mock: bool,
    connection_status: String,
    last_payload_utc: Option<DateTime<Utc>>,
    last_payload_at: Option<Instant>,
    latest_live_payload: Option<GsiPayload>,
    last_publish: Option<Instant>,
    last_fetch_at: Option<Instant>,
    last_fetch_key: String,
    in_flight_key: Option<String>,
    fetch_failed: bool,
    fetch_message: String,
    roster_source: String,
    generation: u64,
    fetch_task: Option<tauri::async_runtime::JoinHandle<()>>,
}

pub struct MatchStore {
    inner: Mutex<Inner>,
    resolver: Arc<dyn RosterResolver>,
    on_change: Box<dyn Fn() + Send + Sync>,
    mock_snapshot: fn() -> MatchSnapshot,
}

impl MatchStore {
    pub fn new(
        resolver: Arc<dyn RosterResolver>,
        mock_snapshot: fn() -> MatchSnapshot,
        on_change: impl Fn() + Send + Sync + 'static,
    ) -> Arc<Self> {
        Arc::new(Self {
            inner: Mutex::new(Inner {
                snapshot: MatchSnapshot::default(),
                page: MatchPageState::default(),
                is_gsi_connected: false,
                is_mock: false,
                connection_status: WAITING.into(),
                last_payload_utc: None,
                last_payload_at: None,
                latest_live_payload: None,
                last_publish: None,
                last_fetch_at: None,
                last_fetch_key: String::new(),
                in_flight_key: None,
                fetch_failed: false,
                fetch_message: String::new(),
                roster_source: String::new(),
                generation: 0,
                fetch_task: None,
            }),
            resolver,
            on_change: Box::new(on_change),
            mock_snapshot,
        })
    }

    fn lock(&self) -> std::sync::MutexGuard<'_, Inner> {
        self.inner.lock().unwrap_or_else(|p| p.into_inner())
    }

    pub fn state(&self) -> StoreState {
        let inner = self.lock();
        StoreState {
            snapshot: inner.snapshot.clone(),
            page_snapshot: if inner.is_mock { inner.snapshot.clone() } else { inner.page.snapshot().clone() },
            is_showing_previous_match: !inner.is_mock && inner.page.is_previous_match(),
            is_gsi_connected: inner.is_gsi_connected,
            is_using_mock_data: inner.is_mock,
            connection_status: inner.connection_status.clone(),
            last_payload_utc: inner.last_payload_utc,
            roster_fetch_failed: inner.fetch_failed,
            roster_fetch_message: inner.fetch_message.clone(),
            roster_source: inner.roster_source.clone(),
        }
    }

    pub fn apply_payload(self: &Arc<Self>, payload: GsiPayload) {
        let snapshot = {
            let mut inner = self.lock();
            inner.last_payload_at = Some(Instant::now());
            inner.latest_live_payload = Some(payload.clone());
            if inner.is_mock {
                return;
            }
            let incoming = gsi::normalize(&payload);
            let snapshot = retain_richer_roster(&inner.snapshot, incoming);
            let spectating = payload.player.as_ref().is_some_and(|p| p.is_spectating())
                || payload.hero.as_ref().is_some_and(|h| h.is_spectating());
            let status = if !snapshot.is_in_match() {
                "Connected (menu/lobby)"
            } else if spectating {
                "Live match — Spectator"
            } else {
                "Live match — Playing"
            };
            if inner.last_publish.is_none_or(|t| t.elapsed() >= MIN_PUBLISH_INTERVAL) {
                publish(&mut inner, snapshot.clone(), status, true);
                drop(inner);
                (self.on_change)();
            }
            snapshot
        };
        self.queue_roster_fetch(snapshot);
    }

    pub fn apply_mock(&self) {
        {
            let mut inner = self.lock();
            if inner.is_mock {
                return;
            }
            cancel_fetch(&mut inner);
            inner.is_mock = true;
            let mut snapshot = (self.mock_snapshot)();
            let mut id = [0u8; 16];
            let _ = getrandom::fill(&mut id);
            snapshot.match_id = format!("debug-{}", hex::encode(id));
            publish(&mut inner, snapshot, "Mock data", false);
        }
        (self.on_change)();
    }

    pub fn clear_mock(self: &Arc<Self>) {
        let latest = {
            let mut inner = self.lock();
            if !inner.is_mock {
                return;
            }
            inner.is_mock = false;
            inner.latest_live_payload.clone()
        };
        self.mark_disconnected();
        if let Some(payload) = latest {
            self.apply_payload(payload);
        }
    }

    pub fn mark_disconnected(&self) {
        {
            let mut inner = self.lock();
            if inner.is_mock {
                return;
            }
            inner.latest_live_payload = None;
            cancel_fetch(&mut inner);
            inner.is_gsi_connected = false;
            inner.connection_status = WAITING.into();
            inner.snapshot = MatchSnapshot { is_connected: false, ..Default::default() };
            let disconnected = inner.snapshot.clone();
            inner.page.update(disconnected);
            inner.last_payload_utc = None;
            inner.last_payload_at = None;
            inner.last_publish = None;
            inner.last_fetch_key.clear();
            inner.last_fetch_at = None;
            inner.in_flight_key = None;
            inner.fetch_failed = false;
            inner.fetch_message.clear();
            inner.roster_source.clear();
        }
        (self.on_change)();
    }

    /// Marks the connection lost after [`STALE_AFTER`] without payloads. Returns whether it did.
    pub fn check_stale(&self) -> bool {
        let stale = {
            let inner = self.lock();
            !inner.is_mock && inner.is_gsi_connected && inner.last_payload_at.is_some_and(|t| t.elapsed() >= STALE_AFTER)
        };
        if stale {
            self.mark_disconnected();
        }
        stale
    }

    pub fn refresh_draft_teams(self: &Arc<Self>) {
        let snapshot = {
            let inner = self.lock();
            if inner.is_mock || !inner.is_gsi_connected || !inner.snapshot.is_in_draft()
                || roster::count_teamed(&inner.snapshot.players) >= 10
            {
                return;
            }
            inner.snapshot.clone()
        };
        self.queue_roster_fetch(snapshot);
    }

    fn queue_roster_fetch(self: &Arc<Self>, snapshot: MatchSnapshot) {
        if !snapshot.is_connected || !roster::needs_refresh(&snapshot.players) {
            return;
        }
        let local_account = steam_id::to_account_id(&snapshot.steam_id);
        if !match_id::is_usable(&snapshot.match_id) && local_account.is_none() && snapshot.server_steam_id.trim().is_empty() {
            return;
        }

        let strategy_time = snapshot.game_state == "DOTA_GAMERULES_STATE_STRATEGY_TIME";
        let key = format!(
            "{}|{}|{}|{strategy_time}",
            snapshot.match_id,
            snapshot.server_steam_id,
            local_account.map(|a| a.to_string()).unwrap_or_default()
        );

        let generation = {
            let mut inner = self.lock();
            if inner.is_mock || inner.in_flight_key.as_deref() == Some(key.as_str()) {
                return;
            }
            // Retry throughout strategy time while Steam is still filling in the teams.
            let teamed = roster::count_teamed(&snapshot.players);
            let awaiting_teams = !snapshot.server_steam_id.trim().is_empty() && teamed < 10;
            let retry = if strategy_time && teamed < 10 {
                STRATEGY_ROSTER_FETCH_INTERVAL
            } else if inner.fetch_failed || awaiting_teams || (snapshot.is_in_draft() && teamed < 10) {
                FAILED_ROSTER_FETCH_INTERVAL
            } else {
                MIN_ROSTER_FETCH_INTERVAL
            };
            if inner.last_fetch_key == key && inner.last_fetch_at.is_some_and(|t| t.elapsed() < retry) {
                return;
            }
            cancel_fetch(&mut inner);
            inner.last_fetch_key = key.clone();
            inner.last_fetch_at = Some(Instant::now());
            inner.in_flight_key = Some(key.clone());
            inner.generation
        };

        let store = Arc::clone(self);
        let task_key = key.clone();
        let handle = tauri::async_runtime::spawn(async move {
            let result = tokio::time::timeout(ROSTER_FETCH_TIMEOUT, store.resolver.resolve(&snapshot)).await;
            store.finish_fetch(&snapshot, generation, &task_key, result.ok());
        });
        let mut inner = self.lock();
        // Keep the handle only while this fetch is still the current one, so cancel can abort it.
        if inner.generation == generation && inner.in_flight_key.as_deref() == Some(key.as_str()) {
            inner.fetch_task = Some(handle);
        }
    }

    /// `result`: `None` on timeout; `Some(None)` when no source found anything.
    fn finish_fetch(&self, requested: &MatchSnapshot, generation: u64, key: &str, result: Option<Option<RosterResolution>>) {
        let changed = {
            let mut inner = self.lock();
            if inner.generation != generation {
                return;
            }
            if inner.in_flight_key.as_deref() == Some(key) {
                inner.in_flight_key = None;
            }
            inner.fetch_task = None;
            if inner.is_mock || !inner.snapshot.is_connected {
                return;
            }

            let Some(resolution) = result else {
                inner.fetch_failed = true;
                inner.fetch_message = "Lobby lookup timed out — retrying.".into();
                drop(inner);
                (self.on_change)();
                return;
            };

            let gsi_players = &requested.players;
            let server = resolution.as_ref().and_then(|r| r.server_steam_id.clone()).filter(|s| !s.is_empty());
            let external = resolution.as_ref().map(|r| r.players.clone()).filter(|p| !p.is_empty());
            let not_richer = |players: &[mango_core::model::MatchPlayer], than: &[mango_core::model::MatchPlayer]| {
                roster::count_identified(players) <= roster::count_identified(than)
                    && roster::count_heroes(players) <= roster::count_heroes(than)
                    && roster::count_teamed(players) <= roster::count_teamed(than)
            };
            let same_match = !match_id::is_usable(&requested.match_id)
                || mango_core::text::eq_ignore_case(&inner.snapshot.match_id, &requested.match_id);

            match external.filter(|e| !not_richer(e, gsi_players)) {
                None => {
                    inner.fetch_failed = true;
                    inner.fetch_message = if server.is_none() {
                        "This game is not in Steam's public live list, so the other players stay hidden until the match is parsed."
                    } else {
                        "Found the live server, but Steam did not return the other players yet."
                    }
                    .into();
                    if let Some(server) = server.filter(|s| same_match && *s != inner.snapshot.server_steam_id) {
                        let mut updated = inner.snapshot.clone();
                        updated.server_steam_id = server;
                        let status = inner.connection_status.clone();
                        publish(&mut inner, updated, &status, true);
                    }
                    true
                }
                Some(external) => {
                    inner.fetch_failed = false;
                    inner.fetch_message.clear();
                    let merged = roster::merge(gsi_players, &external);
                    if !same_match || (not_richer(&merged, &inner.snapshot.players) && server.is_none()) {
                        true
                    } else {
                        let mut updated = inner.snapshot.clone();
                        updated.players = roster::merge(&merged, &inner.snapshot.players);
                        if let Some(server) = server {
                            updated.server_steam_id = server;
                        }
                        inner.roster_source = resolution.map(|r| r.source).unwrap_or_default();
                        let status = inner.connection_status.clone();
                        publish(&mut inner, updated, &status, true);
                        true
                    }
                }
            }
        };
        if changed {
            (self.on_change)();
        }
    }
}

fn cancel_fetch(inner: &mut Inner) {
    if let Some(task) = inner.fetch_task.take() {
        task.abort();
    }
    inner.in_flight_key = None;
    inner.generation += 1;
}

fn publish(inner: &mut Inner, snapshot: MatchSnapshot, status: &str, connected: bool) {
    inner.last_publish = Some(Instant::now());
    if !inner.is_mock {
        inner.page.update(snapshot.clone());
    }
    inner.last_payload_utc = Some(snapshot.received_at_utc);
    inner.snapshot = snapshot;
    inner.is_gsi_connected = connected;
    inner.connection_status = status.into();
}

/// Playing GSI usually sends only the local player; keep the fetched lobby for the same match.
fn retain_richer_roster(current: &MatchSnapshot, mut incoming: MatchSnapshot) -> MatchSnapshot {
    if !current.is_connected || !incoming.is_in_match() {
        return incoming;
    }
    incoming.players = roster::retain_if_richer(&incoming.players, &current.players, &incoming.match_id, &current.match_id);
    if incoming.server_steam_id.trim().is_empty()
        && match_id::is_usable(&incoming.match_id)
        && incoming.match_id == current.match_id
    {
        incoming.server_steam_id = current.server_steam_id.clone();
    }
    incoming
}

#[cfg(test)]
mod tests {
    use std::sync::atomic::{AtomicUsize, Ordering};

    use async_trait::async_trait;
    use mango_core::model::MatchPlayer;

    use super::*;

    struct FakeResolver {
        result: Option<RosterResolution>,
        calls: AtomicUsize,
    }

    #[async_trait]
    impl RosterResolver for FakeResolver {
        async fn resolve(&self, _: &MatchSnapshot) -> Option<RosterResolution> {
            self.calls.fetch_add(1, Ordering::SeqCst);
            self.result.clone()
        }
    }

    fn lobby() -> Vec<MatchPlayer> {
        (1..=10)
            .map(|i| MatchPlayer {
                steam_id: i.to_string(),
                name: format!("P{i}"),
                team_name: if i <= 5 { "radiant" } else { "dire" }.into(),
                hero_name: "npc_dota_hero_axe".into(),
                ..Default::default()
            })
            .collect()
    }

    fn store(result: Option<RosterResolution>) -> (Arc<MatchStore>, Arc<FakeResolver>, Arc<AtomicUsize>) {
        let resolver = Arc::new(FakeResolver { result, calls: AtomicUsize::new(0) });
        let changes = Arc::new(AtomicUsize::new(0));
        let counter = changes.clone();
        let store = MatchStore::new(resolver.clone(), MatchSnapshot::default, move || {
            counter.fetch_add(1, Ordering::SeqCst);
        });
        (store, resolver, changes)
    }

    fn playing(match_id: &str) -> GsiPayload {
        GsiPayload::parse(&format!(
            r#"{{"map":{{"matchid":"{match_id}","game_state":"DOTA_GAMERULES_STATE_GAME_IN_PROGRESS"}},
                "player":{{"steamid":"1","name":"Me","team_name":"radiant"}}}}"#
        ))
        .unwrap()
    }

    async fn settle() {
        for _ in 0..50 {
            tokio::task::yield_now().await;
            tokio::time::sleep(Duration::from_millis(5)).await;
        }
    }

    #[tokio::test]
    async fn sparse_gsi_gets_the_lobby_from_external_sources() {
        let (store, resolver, _) = store(Some(RosterResolution {
            players: lobby(),
            server_steam_id: Some("900".into()),
            source: "steam-realtime".into(),
        }));

        store.apply_payload(playing("100"));
        settle().await;

        let state = store.state();
        assert_eq!(resolver.calls.load(Ordering::SeqCst), 1);
        assert_eq!(state.snapshot.players.len(), 10);
        assert_eq!(state.snapshot.server_steam_id, "900");
        assert_eq!(state.roster_source, "steam-realtime");
        assert_eq!(state.connection_status, "Live match — Playing");
        assert_eq!(state.page_snapshot.players.len(), 10);
        assert_eq!(state.snapshot.players.iter().find(|p| p.steam_id == "1").unwrap().name, "Me");
    }

    #[tokio::test]
    async fn failed_lookup_explains_why_and_keeps_gsi_roster() {
        let (store, _, _) = store(None);
        store.apply_payload(playing("100"));
        settle().await;
        let state = store.state();
        assert!(state.roster_fetch_failed);
        assert!(state.roster_fetch_message.contains("not in Steam's public live list"));
        assert_eq!(state.snapshot.players.len(), 1);
    }

    #[tokio::test]
    async fn server_only_lookup_records_the_server() {
        let (store, _, _) = store(Some(RosterResolution { players: Vec::new(), server_steam_id: Some("77".into()), source: "local-steam".into() }));
        store.apply_payload(playing("100"));
        settle().await;
        let state = store.state();
        assert_eq!(state.snapshot.server_steam_id, "77");
        assert!(state.roster_fetch_message.contains("Found the live server"));
    }

    #[tokio::test]
    async fn sparse_updates_keep_the_fetched_lobby_and_are_throttled() {
        let (store, resolver, _) = store(Some(RosterResolution { players: lobby(), server_steam_id: None, source: "x".into() }));
        store.apply_payload(playing("100"));
        settle().await;
        store.lock().last_publish = None;
        store.apply_payload(playing("100"));
        settle().await;
        assert_eq!(store.state().snapshot.players.len(), 10, "lobby retained");
        assert_eq!(resolver.calls.load(Ordering::SeqCst), 1, "complete roster needs no refetch");
    }

    #[tokio::test]
    async fn mock_data_replaces_live_data_until_cleared() {
        let (store, _, _) = store(None);
        store.apply_mock();
        let mock = store.state();
        assert!(mock.is_using_mock_data);
        assert!(mock.snapshot.match_id.starts_with("debug-"));
        assert_eq!(mock.connection_status, "Mock data");

        store.apply_payload(playing("100"));
        assert!(store.state().is_using_mock_data, "live payloads ignored while mocking");

        store.clear_mock();
        let live = store.state();
        assert!(!live.is_using_mock_data);
        assert_eq!(live.snapshot.match_id, "100", "latest live payload replayed");
    }

    #[tokio::test]
    async fn stale_connection_returns_to_waiting_but_keeps_the_last_match() {
        let (store, _, _) = store(Some(RosterResolution { players: lobby(), server_steam_id: None, source: "x".into() }));
        store.apply_payload(playing("100"));
        settle().await;
        assert!(!store.check_stale());
        store.lock().last_payload_at = Some(Instant::now() - STALE_AFTER);
        assert!(store.check_stale());
        let state = store.state();
        assert!(!state.is_gsi_connected);
        assert_eq!(state.connection_status, WAITING);
        assert_eq!(state.page_snapshot.match_id, "100");
        assert!(state.is_showing_previous_match);
    }

    #[tokio::test]
    async fn menu_payloads_do_not_fetch() {
        let (store, resolver, changes) = store(None);
        store.apply_payload(GsiPayload::parse(r#"{"provider":{"name":"Dota 2"}}"#).unwrap());
        settle().await;
        assert_eq!(resolver.calls.load(Ordering::SeqCst), 0);
        assert_eq!(store.state().connection_status, "Connected (menu/lobby)");
        assert_eq!(changes.load(Ordering::SeqCst), 1);
    }
}
