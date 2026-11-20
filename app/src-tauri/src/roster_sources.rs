//! Finds the lobby when GSI only knows the local player (C# `AppServices.ResolveMatchRosterAsync`).
//!
//! Local Steam, OpenDota's live list and, with a key, Steam's live endpoints are asked in
//! parallel. The richest roster wins; once one is in hand, slower sources get one more second.

use std::future::Future;
use std::pin::Pin;
use std::sync::Arc;
use std::time::Duration;

use async_trait::async_trait;
use futures::stream::{FuturesUnordered, StreamExt};
use mango_core::model::{MatchPlayer, MatchSnapshot};
use mango_core::roster;
use mango_core::steam_id;

use crate::opendota::OpenDota;
use crate::settings::SettingsStore;
use crate::steam_api::SteamApi;
use crate::steam_roster::LocalSteamRoster;

#[derive(Debug, Clone, Default, PartialEq)]
pub struct RosterResolution {
    pub players: Vec<MatchPlayer>,
    pub server_steam_id: Option<String>,
    pub source: String,
}

impl RosterResolution {
    pub fn identified(&self) -> usize {
        roster::count_identified(&self.players)
    }

    fn is_richer_than(&self, best: &RosterResolution) -> bool {
        let key = |r: &RosterResolution| (roster::count_teamed(&r.players), r.identified(), roster::count_heroes(&r.players));
        key(self) > key(best)
    }
}

#[async_trait]
pub trait RosterResolver: Send + Sync {
    async fn resolve(&self, snapshot: &MatchSnapshot) -> Option<RosterResolution>;
}

type Source = Pin<Box<dyn Future<Output = Option<RosterResolution>> + Send>>;

/// The richest of `sources`; stops early once a roster is complete.
pub async fn first_rich_roster(sources: Vec<Source>, grace: Duration) -> Option<RosterResolution> {
    let mut pending: FuturesUnordered<Source> = sources.into_iter().collect();
    let mut best: Option<RosterResolution> = None;
    loop {
        let next = match &best {
            None => pending.next().await,
            Some(_) => match tokio::time::timeout(grace, pending.next()).await {
                Ok(next) => next,
                Err(_) => return best,
            },
        };
        let Some(result) = next else { return best };
        let Some(result) = result else { continue };
        if result.identified() == 0 && result.server_steam_id.as_deref().is_none_or(str::is_empty) {
            continue;
        }
        if best.as_ref().is_none_or(|b| result.is_richer_than(b)) {
            best = Some(result);
        }
        if best.as_ref().is_some_and(|b| !roster::needs_refresh(&b.players)) {
            return best;
        }
    }
}

fn resolution(players: Option<Vec<MatchPlayer>>, server: Option<String>, source: &str) -> Option<RosterResolution> {
    let players = players.unwrap_or_default();
    let server = server.filter(|s| !s.trim().is_empty());
    if players.is_empty() && server.is_none() {
        return None;
    }
    Some(RosterResolution { players, server_steam_id: server, source: source.into() })
}

pub struct LiveRosterResolver {
    pub settings: Arc<SettingsStore>,
    pub opendota: Arc<OpenDota>,
    pub steam: Arc<SteamApi>,
    pub local: Arc<LocalSteamRoster>,
}

#[async_trait]
impl RosterResolver for LiveRosterResolver {
    async fn resolve(&self, snapshot: &MatchSnapshot) -> Option<RosterResolution> {
        let local_account = steam_id::to_account_id(&snapshot.steam_id);
        let mut sources: Vec<Source> = Vec::new();

        let (local, snap) = (self.local.clone(), snapshot.clone());
        sources.push(Box::pin(async move {
            let (players, server) = local.roster(&snap).await?;
            resolution(Some(players), Some(server), "local-steam")
        }));

        let (opendota, match_id) = (self.opendota.clone(), snapshot.match_id.clone());
        sources.push(Box::pin(async move {
            resolution(opendota.match_roster(&match_id, local_account).await, None, "opendota-live")
        }));

        let key = self.settings.read().steam_api_key.clone();
        if !key.is_empty() {
            // Hero icons can stay empty; identities and teams still load.
            let heroes = tokio::time::timeout(Duration::from_secs(2), self.opendota.hero_internal_names())
                .await
                .unwrap_or_default();
            let heroes = Arc::new(heroes);

            let (steam, key2, match_id, heroes2) = (self.steam.clone(), key.clone(), snapshot.match_id.clone(), heroes.clone());
            sources.push(Box::pin(async move {
                let hit = steam.find_top_live_game(local_account, &match_id, &key2, &heroes2).await?;
                resolution(Some(hit.players), Some(hit.server_steam_id), "steam-top-live")
            }));

            let server = if snapshot.server_steam_id.trim().is_empty() {
                self.local.server_for(&snapshot.match_id).map(|(server, _)| server)
            } else {
                Some(snapshot.server_steam_id.clone())
            };
            if let Some(server) = server {
                let (steam, match_id) = (self.steam.clone(), snapshot.match_id.clone());
                sources.push(Box::pin(async move {
                    let players = steam.realtime_roster(&server, &key, &heroes, &match_id).await;
                    resolution(players, Some(server), "steam-realtime")
                }));
            }
        }

        first_rich_roster(sources, Duration::from_secs(5)).await
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn players(n: usize, teamed: bool, heroes: bool) -> Vec<MatchPlayer> {
        (0..n)
            .map(|i| MatchPlayer {
                steam_id: (i + 1).to_string(),
                team_name: if teamed { if i < 5 { "radiant" } else { "dire" } } else { "" }.into(),
                hero_name: if heroes { "npc_dota_hero_axe".into() } else { String::new() },
                ..Default::default()
            })
            .collect()
    }

    fn source(delay_ms: u64, result: Option<RosterResolution>) -> Source {
        Box::pin(async move {
            tokio::time::sleep(Duration::from_millis(delay_ms)).await;
            result
        })
    }

    fn found(players: Vec<MatchPlayer>, source: &str) -> Option<RosterResolution> {
        Some(RosterResolution { players, server_steam_id: None, source: source.into() })
    }

    #[tokio::test(start_paused = true)]
    async fn complete_roster_returns_immediately() {
        let best = first_rich_roster(
            vec![source(10, found(players(10, true, true), "fast")), source(10_000, found(players(10, true, true), "slow"))],
            Duration::from_secs(1),
        )
        .await
        .unwrap();
        assert_eq!(best.source, "fast");
    }

    #[tokio::test(start_paused = true)]
    async fn richer_late_roster_wins_within_grace() {
        let best = first_rich_roster(
            vec![source(10, found(players(10, false, false), "ids")), source(500, found(players(10, true, false), "teams"))],
            Duration::from_secs(1),
        )
        .await
        .unwrap();
        assert_eq!(best.source, "teams");
    }

    #[tokio::test(start_paused = true)]
    async fn slow_sources_get_one_second_after_a_result() {
        let best = first_rich_roster(
            vec![source(10, found(players(2, false, false), "partial")), source(5_000, found(players(10, true, true), "late"))],
            Duration::from_secs(1),
        )
        .await
        .unwrap();
        assert_eq!(best.source, "partial");
    }

    #[tokio::test(start_paused = true)]
    async fn server_only_results_count_and_empty_ones_do_not() {
        let server_only = Some(RosterResolution { players: Vec::new(), server_steam_id: Some("9".into()), source: "srv".into() });
        let best = first_rich_roster(vec![source(10, found(Vec::new(), "empty")), source(20, server_only)], Duration::from_secs(1))
            .await
            .unwrap();
        assert_eq!(best.source, "srv");
        assert!(first_rich_roster(vec![source(1, None)], Duration::from_secs(1)).await.is_none());
    }
}
