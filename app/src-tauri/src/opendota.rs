//! OpenDota API with a file cache (C# `OpenDotaService`).
//!
//! Every response is cached under `%LOCALAPPDATA%\Mango\cache`; when the network fails or
//! OpenDota returns an error, the last cached copy is used instead.

use std::collections::HashMap;
use std::path::PathBuf;
use std::sync::{Arc, Mutex};
use std::time::{Duration, SystemTime};

use async_trait::async_trait;
use chrono::{Local, TimeZone, Utc};
use futures::{stream, StreamExt};
use mango_core::history::{format_position_label, EncounterSide, MatchAnalysisOutcome, MatchBuildItem, PlayerMatchHistory, RecentEncounter, RecentMatchSummary};
use mango_core::json::{as_i32, as_i64, as_u64};
use mango_core::match_build::{self, ItemConstant};
use mango_core::model::{hero_icon_key, MatchPlayer};
use mango_core::rank_estimate::{self, RankEstimate, RankedMatchSample};
use mango_core::text::{eq_ignore_case, is_blank};
use mango_core::{assets, match_id, steam_id};
use serde::Serialize;
use serde_json::Value;
use tokio::sync::{OnceCell, Semaphore};

use crate::http::{Request, SharedHttp};

pub const BASE_URL: &str = "https://api.opendota.com/api/";
const SLIM_MATCH_CACHE_BYTES: u64 = 4096;
const FINAL_SLOTS: [&str; 10] =
    ["item_0", "item_1", "item_2", "item_3", "item_4", "item_5", "backpack_0", "backpack_1", "backpack_2", "item_neutral"];

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Hero {
    pub id: i32,
    /// `npc_dota_hero_axe`
    pub name: String,
    pub localized_name: String,
}

#[derive(Debug, Clone, Default, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PlayerOverview {
    pub avatar_url: String,
    pub steam_id: String,
    pub display_name: String,
    pub rank_tier: Option<i32>,
    pub leaderboard_rank: Option<i32>,
    pub mmr_estimate: Option<i32>,
    pub history_rank_estimate: Option<RankEstimate>,
    pub display_rank_tier: Option<i32>,
    pub rank_is_estimated: bool,
    pub rank_label: String,
    pub wins: i64,
    pub losses: i64,
    pub win_rate_percent: Option<f64>,
    pub possible_smurf: bool,
    pub is_private: bool,
    pub recent_matches: Vec<PlayerMatchRow>,
}

pub(crate) fn possible_smurf(wins: i64, losses: i64) -> bool {
    let Some(total) = wins.checked_add(losses) else { return false };
    wins >= 0 && losses >= 0 && (50..=500).contains(&total) && wins * 100 >= total * 65
}

#[derive(Debug, Clone, Default, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PlayerMatchRow {
    pub match_id: String,
    pub hero_name: String,
    pub hero_image_url: String,
    pub result: String,
    pub analysis_outcome: Option<MatchAnalysisOutcome>,
    pub kda: String,
    pub duration: String,
    pub played_at: String,
}

/// Replay-analysed data from STRATZ, used when available.
#[async_trait]
pub trait ReplaySource: Send + Sync {
    /// Position 1–5 by account id.
    async fn positions(&self, match_id: i64) -> Result<Option<HashMap<i64, i32>>, String>;
    async fn analysis_outcome(&self, match_id: i64) -> Option<MatchAnalysisOutcome>;
    /// `(item_id, seconds)` in time order.
    async fn purchases(&self, match_id: i64, account_id: i64) -> Result<Option<Vec<(i32, i32)>>, String>;
}

pub async fn load_analysis_outcomes(
    replay: Option<&dyn ReplaySource>,
    match_ids: impl Iterator<Item = i64>,
) -> HashMap<i64, MatchAnalysisOutcome> {
    let Some(replay) = replay else { return HashMap::new() };
    let ids: std::collections::HashSet<_> = match_ids.filter(|id| *id > 0).collect();
    stream::iter(ids)
        .map(|id| async move { replay.analysis_outcome(id).await.map(|outcome| (id, outcome)) })
        .buffer_unordered(3)
        .filter_map(std::future::ready)
        .collect()
        .await
}

pub struct OpenDota {
    http: SharedHttp,
    base_url: String,
    cache_dir: PathBuf,
    heroes: OnceCell<Arc<Vec<Hero>>>,
    request_throttle: Semaphore,
    match_detail_throttle: Semaphore,
    cache_locks: Mutex<HashMap<String, Arc<tokio::sync::Mutex<()>>>>,
    /// Match id each player's recent matches were last force-refreshed for.
    recent_refreshed_for: Mutex<HashMap<u64, String>>,
    /// Waits between retries; zero in tests.
    retry_delay: Duration,
}

impl OpenDota {
    pub fn new(http: SharedHttp, cache_dir: PathBuf) -> Self {
        let _ = std::fs::create_dir_all(&cache_dir);
        Self {
            http,
            base_url: BASE_URL.into(),
            cache_dir,
            heroes: OnceCell::new(),
            request_throttle: Semaphore::new(6),
            match_detail_throttle: Semaphore::new(4),
            cache_locks: Mutex::new(HashMap::new()),
            recent_refreshed_for: Mutex::new(HashMap::new()),
            retry_delay: Duration::from_secs(1),
        }
    }

    #[cfg(test)]
    fn for_tests(http: SharedHttp, cache_dir: PathBuf) -> Self {
        Self { retry_delay: Duration::ZERO, ..Self::new(http, cache_dir) }
    }

    // ---- Cache ---------------------------------------------------------------------------

    /// Cached JSON for `endpoint`. `max_age: None` keeps the cache forever.
    async fn get_cached(
        &self,
        cache_file: impl Into<String>,
        endpoint: impl Into<String>,
        max_age: Option<Duration>,
    ) -> Option<Value> {
        let (cache_file, endpoint) = (cache_file.into(), endpoint.into());
        let gate = {
            let mut locks = self.cache_locks.lock().unwrap();
            locks.entry(cache_file.clone()).or_default().clone()
        };
        let _guard = gate.lock().await;

        let path = self.cache_dir.join(&cache_file);
        let stale = tokio::fs::read_to_string(&path).await.ok();
        if let Some(text) = &stale {
            let age = std::fs::metadata(&path)
                .and_then(|m| m.modified())
                .ok()
                .and_then(|t| SystemTime::now().duration_since(t).ok())
                .unwrap_or_default();
            if max_age.is_none_or(|max| age <= max) {
                if let Some(value) = parse(text) {
                    return Some(value);
                }
            }
        }

        for attempt in 0..3 {
            let response = {
                let _permit = self.request_throttle.acquire().await.ok()?;
                self.http.send(Request::get(format!("{}{endpoint}", self.base_url))).await
            };
            let Ok(response) = response else { break };
            if response.is_success() {
                let text = response.text();
                let _ = tokio::fs::write(&path, &text).await;
                return parse(&text);
            }
            if !is_transient(response.status) || attempt == 2 {
                break;
            }
            tokio::time::sleep(self.retry_delay * (1 << attempt)).await;
        }
        stale.as_deref().and_then(parse)
    }

    // ---- Heroes --------------------------------------------------------------------------

    pub async fn heroes(&self) -> Result<Arc<Vec<Hero>>, String> {
        self.heroes
            .get_or_try_init(|| async {
                let value = self.get_cached("heroes.json", "heroes", None).await.ok_or("Failed to load OpenDota heroes")?;
                let heroes = value
                    .as_array()
                    .map(|rows| {
                        rows.iter()
                            .map(|h| Hero {
                                id: h.get("id").and_then(as_i32).unwrap_or_default(),
                                name: str_of(h, "name"),
                                localized_name: str_of(h, "localized_name"),
                            })
                            .collect()
                    })
                    .unwrap_or_default();
                Ok::<_, String>(Arc::new(heroes))
            })
            .await
            .cloned()
    }

    /// Hero id → `npc_dota_hero_*` name; empty when heroes cannot load.
    pub async fn hero_internal_names(&self) -> HashMap<i32, String> {
        let mut map = HashMap::new();
        for hero in self.heroes().await.map(|h| h.to_vec()).unwrap_or_default() {
            map.entry(hero.id).or_insert(hero.name);
        }
        map
    }

    /// `pudge` or `Pudge` → 14.
    pub fn resolve_hero_id(hero_name: &str, heroes: &[Hero]) -> Option<i32> {
        if is_blank(hero_name) {
            return None;
        }
        let internal = format!("npc_dota_hero_{hero_name}");
        heroes
            .iter()
            .find(|h| eq_ignore_case(&h.name, &internal) || eq_ignore_case(&h.localized_name, hero_name))
            .map(|h| h.id)
    }

    // ---- Live roster ---------------------------------------------------------------------

    /// Players of a live match from OpenDota's live list (only top games are listed).
    pub async fn match_roster(&self, match_id: &str, local_account: Option<u64>) -> Option<Vec<MatchPlayer>> {
        if !match_id::is_usable(match_id) && local_account.is_none() {
            return None;
        }
        let hero_names = self.hero_internal_names().await;
        let games = self.get_cached("live_games.json", "live", Some(Duration::from_secs(5))).await?;
        let games = games.as_array()?;
        let game_match = |g: &Value| g.get("match_id").map(flexible).unwrap_or_default();

        let by_match = match_id::is_usable(match_id)
            .then(|| games.iter().find(|g| eq_ignore_case(&game_match(g), match_id)))
            .flatten();
        let game = by_match.or_else(|| {
            let account = local_account?;
            games.iter().find(|g| {
                (!match_id::is_usable(match_id) || !match_id::is_usable(&game_match(g)))
                    && players_of(g).any(|p| p.get("account_id").and_then(as_u64) == Some(account))
            })
        })?;

        let players: Vec<MatchPlayer> = players_of(game)
            .filter_map(|p| {
                let account = p.get("account_id").and_then(as_u64).filter(|a| *a > 0)?;
                let team = p.get("team").and_then(as_i32).unwrap_or_default();
                let slot = p.get("team_slot").and_then(as_i32).unwrap_or_default();
                let hero = p.get("hero_id").and_then(as_i32).unwrap_or_default();
                Some(MatchPlayer {
                    steam_id: account.to_string(),
                    name: account.to_string(),
                    team_name: if team == 0 { "radiant" } else { "dire" }.into(),
                    team_slot: (slot - 1).max(0),
                    hero_name: hero_names.get(&hero).cloned().unwrap_or_default(),
                    ..Default::default()
                })
            })
            .collect();
        (!players.is_empty()).then_some(players)
    }

    // ---- Player overview -----------------------------------------------------------------

    pub async fn player_overview(&self, steam_id_text: &str) -> Option<PlayerOverview> {
        let account = steam_id::to_account_id(steam_id_text)?;
        let (heroes, profile, win_loss, recent, ranked) = tokio::join!(
            self.heroes(),
            self.get_cached(format!("player_{account}.json"), format!("players/{account}"), Some(hours(6))),
            self.get_cached(
                format!("player_{account}_wl_all_modes.json"),
                format!("players/{account}/wl?significant=0"),
                Some(hours(1))
            ),
            self.get_cached(
                format!("player_{account}_recent.json"),
                format!("players/{account}/recentMatches"),
                Some(Duration::from_secs(15 * 60))
            ),
            self.get_cached(
                format!("player_{account}_ranked_recent.json"),
                format!("players/{account}/matches?lobby_type=7&limit=10&sort=start_time&significant=0"),
                Some(Duration::ZERO)
            ),
        );
        if profile.is_none() && win_loss.is_none() && recent.is_none() && ranked.is_none() {
            return None;
        }

        let profile = profile.unwrap_or(Value::Null);
        let details = profile.get("profile").cloned().unwrap_or(Value::Null);
        let rank_tier = profile.get("rank_tier").and_then(as_i32);
        let leaderboard = profile.get("leaderboard_rank").and_then(as_i32);
        let mmr = profile.get("mmr_estimate").and_then(|m| m.get("estimate")).and_then(as_i32).filter(|m| *m > 0);
        let is_private = details.get("fh_unavailable").and_then(Value::as_bool) == Some(true);
        let history = if rank_tier.is_some_and(|t| t > 0) && !is_private {
            None
        } else {
            self.estimate_from_teammate_ranks(ranked.as_ref()).await
        };
        let resolved = rank_estimate::resolve(rank_tier, leaderboard, history, mmr, None, is_private);

        let wins = win_loss.as_ref().and_then(|w| w.get("win")).and_then(as_i64);
        let losses = win_loss.as_ref().and_then(|w| w.get("lose")).and_then(as_i64);
        let possible_smurf = !is_private && wins.zip(losses).is_some_and(|(wins, losses)| possible_smurf(wins, losses));
        let wins = wins.unwrap_or_default();
        let losses = losses.unwrap_or_default();
        let total = wins + losses;
        let avatar = ["avatarfull", "avatarmedium", "avatar"]
            .iter()
            .filter_map(|key| details.get(*key).and_then(Value::as_str))
            .find(|url| !is_blank(url))
            .map(|url| url.trim().to_string())
            .unwrap_or_default();
        let persona = details.get("personaname").and_then(Value::as_str).unwrap_or_default();

        let heroes = heroes.unwrap_or_default();
        Some(PlayerOverview {
            steam_id: steam_id_text.trim().into(),
            display_name: [persona, steam_id_text]
                .iter()
                .find(|v| !is_blank(v))
                .map_or("—".into(), |v| v.trim().to_string()),
            avatar_url: avatar,
            rank_tier,
            leaderboard_rank: leaderboard,
            mmr_estimate: mmr,
            history_rank_estimate: history,
            display_rank_tier: resolved.rank_tier,
            rank_is_estimated: resolved.is_estimated,
            rank_label: if is_blank(&resolved.label) { "—".into() } else { resolved.label },
            wins,
            losses,
            win_rate_percent: (total > 0).then(|| (wins as f64 * 1000.0 / total as f64).round() / 10.0),
            possible_smurf,
            is_private,
            recent_matches: recent
                .as_ref()
                .and_then(Value::as_array)
                .map(|rows| rows.iter().map(|m| to_match_row(m, &heroes)).collect())
                .unwrap_or_default(),
        })
    }

    async fn estimate_from_teammate_ranks(&self, ranked: Option<&Value>) -> Option<RankEstimate> {
        let candidates: Vec<&Value> = ranked
            .and_then(Value::as_array)
            .map(|rows| {
                rows.iter()
                    .filter(|m| {
                        m.get("match_id").and_then(as_i64).is_some_and(|id| id > 0)
                            && m.get("average_rank").and_then(as_i32).is_some_and(|r| r > 0)
                    })
                    .collect()
            })
            .unwrap_or_default();
        let details = futures::future::join_all(
            candidates.iter().map(|m| self.match_player_ranks(m.get("match_id").and_then(as_i64).unwrap_or_default())),
        )
        .await;
        let samples: Vec<RankedMatchSample> = candidates
            .iter()
            .zip(details)
            .map(|(m, players)| RankedMatchSample {
                lobby_type: m.get("lobby_type").and_then(as_i32).unwrap_or_default(),
                player_slot: m.get("player_slot").and_then(as_i32).unwrap_or_default(),
                average_rank: m.get("average_rank").and_then(as_i32),
                players,
            })
            .collect();
        rank_estimate::estimate_from_teammate_ranks(&samples)
    }

    /// `(player_slot, rank_tier)` for a finished match; the cache keeps only those fields.
    async fn match_player_ranks(&self, match_id: i64) -> Option<Vec<(i32, Option<i32>)>> {
        let _permit = self.match_detail_throttle.acquire().await.ok()?;
        let cache_file = format!("match_{match_id}_ranks.json");
        let detail = self.get_cached(&cache_file, format!("matches/{match_id}"), None).await?;
        let players: Vec<(i32, Option<i32>)> = detail
            .get("players")
            .and_then(Value::as_array)?
            .iter()
            .map(|p| (p.get("player_slot").and_then(as_i32).unwrap_or_default(), p.get("rank_tier").and_then(as_i32)))
            .collect();

        let path = self.cache_dir.join(&cache_file);
        if std::fs::metadata(&path).is_ok_and(|m| m.len() > SLIM_MATCH_CACHE_BYTES) {
            // Completed matches never change; keep only what the estimate needs.
            let slim = serde_json::json!({
                "players": players.iter().map(|(slot, tier)| serde_json::json!({"player_slot": slot, "rank_tier": tier})).collect::<Vec<_>>()
            });
            let _ = tokio::fs::write(&path, slim.to_string()).await;
        }
        Some(players)
    }

    // ---- Match history -------------------------------------------------------------------

    pub async fn recent_match_summaries(
        &self,
        steam_id_text: &str,
        excluded_match_id: &str,
        replay: Option<&dyn ReplaySource>,
    ) -> (Vec<RecentMatchSummary>, String) {
        let Some(account) = steam_id::to_account_id(steam_id_text) else {
            return (Vec::new(), "Recent matches unavailable.".into());
        };
        // A new match means the previous one just finished, so skip the cache once per player.
        let refresh = match_id::is_usable(excluded_match_id)
            && self.recent_refreshed_for.lock().unwrap().get(&account).map(String::as_str) != Some(excluded_match_id);
        let max_age = if refresh { Duration::ZERO } else { Duration::from_secs(15 * 60) };
        let matches = self
            .get_cached(format!("player_{account}_recent.json"), format!("players/{account}/recentMatches"), Some(max_age))
            .await;
        if refresh {
            self.recent_refreshed_for.lock().unwrap().insert(account, excluded_match_id.into());
        }
        let Some(matches) = matches.as_ref().and_then(Value::as_array) else {
            return (Vec::new(), "Recent matches unavailable.".into());
        };
        let heroes = self.heroes().await.map(|h| h.to_vec()).unwrap_or_default();

        let mut rows: Vec<&Value> = matches
            .iter()
            .filter(|m| {
                let id = m.get("match_id").and_then(as_i64).unwrap_or_default();
                id > 0 && id.to_string() != excluded_match_id
            })
            .collect();
        rows.sort_by_key(|m| std::cmp::Reverse(m.get("start_time").and_then(as_i64).unwrap_or_default()));
        rows.truncate(5);
        let outcomes = load_analysis_outcomes(replay, rows.iter().filter_map(|m| m.get("match_id").and_then(as_i64))).await;
        let recent: Vec<RecentMatchSummary> = rows
            .into_iter()
            .map(|m| {
                let hero_id = m.get("hero_id").and_then(as_i32).unwrap_or_default();
                let hero = heroes.iter().find(|h| h.id == hero_id);
                let key = hero.map(|h| hero_icon_key(&h.name)).unwrap_or_default();
                RecentMatchSummary {
                    hero_name: hero.map_or(format!("Hero {hero_id}"), |h| h.localized_name.clone()),
                    hero_image_url: assets::hero_icon(&key),
                    result: if won(m) { "Win" } else { "Loss" }.into(),
                    analysis_outcome: m.get("match_id").and_then(as_i64).and_then(|id| outcomes.get(&id).copied()),
                }
            })
            .collect();
        let label = if recent.is_empty() {
            "No recent matches recorded by OpenDota."
        } else {
            "Last 5 completed matches, newest first"
        };
        (recent, label.into())
    }

    /// Latest shared matches and the recorded with/against counts or an availability message.
    pub async fn encounters(&self, local_steam_id: &str, player_steam_id: &str, excluded_match_id: &str) -> (Vec<RecentEncounter>, String) {
        let Some(local) = steam_id::to_account_id(local_steam_id) else {
            return (Vec::new(), "Your account is needed for encounter history.".into());
        };
        let Some(player) = steam_id::to_account_id(player_steam_id) else {
            return (Vec::new(), "Encounter history unavailable.".into());
        };
        if local == player {
            return (Vec::new(), "You".into());
        }
        let quarter_hour = Some(Duration::from_secs(15 * 60));
        let (with, against) = tokio::join!(
            self.get_cached(
                format!("encounters_by_player_{player}_{local}_with.json"),
                format!("players/{player}/matches?with_account_id={local}&significant=0"),
                quarter_hour
            ),
            self.get_cached(
                format!("encounters_by_player_{player}_{local}_against.json"),
                format!("players/{player}/matches?against_account_id={local}&significant=0"),
                quarter_hour
            ),
        );
        let collect = |rows: &[Value], side| {
            rows.iter().filter_map(|row| {
                let id = row.get("match_id").and_then(as_i64).filter(|&id| id > 0)?.to_string();
                if id == excluded_match_id {
                    return None;
                }
                let played_at = row.get("start_time").and_then(as_i64)
                    .filter(|&time| time > 0)
                    .and_then(|time| Utc.timestamp_opt(time, 0).single());
                let hero_id = row.get("hero_id").and_then(as_i32).filter(|&id| id > 0);
                let result = match (row.get("player_slot").and_then(as_i32), row.get("radiant_win").and_then(Value::as_bool)) {
                    (Some(_), Some(_)) if won(row) != (side == EncounterSide::Against) => "Win",
                    (Some(_), Some(_)) => "Loss",
                    _ => "",
                };
                Some((id, side, played_at, hero_id, result))
            }).collect::<Vec<_>>()
        };
        let with = with.as_ref().and_then(Value::as_array);
        let against = against.as_ref().and_then(Value::as_array);
        let mut teammates = collect(with.map_or(&[], Vec::as_slice), EncounterSide::With);
        let opponents = collect(against.map_or(&[], Vec::as_slice), EncounterSide::Against);
        let label = if with.is_none() || against.is_none() {
            if teammates.is_empty() && opponents.is_empty() {
                "Encounter history unavailable.".into()
            } else {
                "Some encounter history is unavailable.".into()
            }
        } else if teammates.is_empty() && opponents.is_empty() {
            "No shared matches recorded.".into()
        } else {
            format!("{} with · {} against", rank_estimate::thousands(teammates.len() as i64), rank_estimate::thousands(opponents.len() as i64))
        };
        teammates.extend(opponents);
        teammates.sort_by_key(|(_, _, played_at, _, _)| std::cmp::Reverse(*played_at));
        teammates.truncate(5);
        let heroes = if teammates.is_empty() {
            None
        } else {
            self.heroes().await.ok()
        };
        let recent = teammates.into_iter().map(|(match_id, side, played_at, hero_id, result)| {
            let hero = heroes.as_ref().and_then(|heroes| heroes.iter().find(|hero| Some(hero.id) == hero_id));
            RecentEncounter {
                match_id,
                side,
                played_at,
                summary: RecentMatchSummary {
                    hero_name: hero.map(|hero| hero.localized_name.clone())
                        .unwrap_or_else(|| hero_id.map_or("Unknown hero".into(), |id| format!("Hero {id}"))),
                    hero_image_url: hero.map(|hero| assets::hero_icon(&hero_icon_key(&hero.name))).unwrap_or_default(),
                    result: result.into(),
                    analysis_outcome: None,
                },
            }
        }).collect();
        (recent, label)
    }

    /// Result, KDA, position and items of the player's latest completed match on `hero_name`.
    pub async fn latest_hero_build(
        &self,
        steam_id_text: &str,
        hero_name: &str,
        excluded_match_id: &str,
        replay: Option<&dyn ReplaySource>,
    ) -> PlayerMatchHistory {
        let unavailable = |label: &str| PlayerMatchHistory { hero_match_label: label.into(), ..Default::default() };
        if is_blank(hero_name) {
            return unavailable("Choose a hero to see their latest build.");
        }
        let Some(account) = steam_id::to_account_id(steam_id_text) else {
            return unavailable("Hero match history unavailable.");
        };
        let Ok(heroes) = self.heroes().await else {
            return unavailable("Hero match history unavailable.");
        };
        let Some(hero_id) = Self::resolve_hero_id(&hero_icon_key(hero_name), &heroes) else {
            return unavailable("Hero match history unavailable.");
        };

        let Some(matches) = self
            .get_cached(
                format!("player_{account}_hero_{hero_id}_latest.json"),
                format!("players/{account}/matches?hero_id={hero_id}&limit=2&significant=0&sort=start_time"),
                Some(Duration::from_secs(15 * 60)),
            )
            .await
        else {
            return unavailable("Hero match history unavailable.");
        };
        let latest = matches.as_array().and_then(|rows| {
            rows.iter()
                .filter(|m| {
                    let id = m.get("match_id").and_then(as_i64).unwrap_or_default();
                    id > 0
                        && m.get("hero_id").and_then(as_i32) == Some(hero_id)
                        && id.to_string() != excluded_match_id
                })
                .max_by_key(|m| m.get("start_time").and_then(as_i64).unwrap_or_default())
        });
        let Some(latest) = latest else {
            return unavailable("No past matches on this hero recorded by OpenDota.");
        };
        let latest_id = latest.get("match_id").and_then(as_i64).unwrap_or_default();

        let detail = self
            .get_cached(format!("match_{latest_id}_build.json"), format!("matches/{latest_id}"), Some(hours(6)))
            .await;
        let detail_players: Vec<Value> =
            detail.as_ref().and_then(|d| d.get("players")).and_then(Value::as_array).cloned().unwrap_or_default();
        let player = detail_players.iter().find(|row| {
            row.get("account_id").and_then(Value::as_u64) == Some(account)
                && row.get("hero_id").and_then(Value::as_i64) == Some(i64::from(hero_id))
        });

        let mut items: Vec<MatchBuildItem> = Vec::new();
        let mut position = None;
        let mut position_basis = "OpenDota parsed-match estimate.".to_string();
        let mut estimated = true;

        let (reported, analysis_outcome) = match replay {
            Some(source) => tokio::join!(source.positions(latest_id), source.analysis_outcome(latest_id)),
            None => (Ok(None), None),
        };
        if let Some(p) = reported.ok().flatten().and_then(|r| r.get(&(account as i64)).copied()).filter(|p| (1..=5).contains(p)) {
            position = Some(p);
            position_basis = "Position from STRATZ replay analysis.".into();
            estimated = false;
        }

        if let Some(player) = player.filter(|p| p.is_object()) {
            let position_est = player.get("position_est").and_then(Value::as_i64).filter(|p| (1..=5).contains(p));
            if position.is_none() && position_est.is_some() {
                position = position_est.map(|p| p as i32);
            } else if position.is_none() {
                position = match_build::estimate_position_from_farm(&detail_players, player);
                position_basis = "Estimated from final team gold per minute and last hits, using lanes when available. May differ from the role played.".into();
            }

            let constants = self.item_constants().await;
            let mut purchases: Vec<(String, i32)> = player
                .get("purchase_log")
                .and_then(Value::as_array)
                .map(|log| {
                    log.iter()
                        .filter_map(|p| {
                            let key = p.get("key")?.as_str()?;
                            let time = p.get("time")?.as_i64().and_then(|t| i32::try_from(t).ok())?;
                            Some((key.to_string(), time))
                        })
                        .collect()
                })
                .unwrap_or_default();

            if purchases.is_empty() {
                if let (Some(source), Some(constants)) = (replay, &constants) {
                    // Unparsed OpenDota matches have no purchase_log; STRATZ has the timeline right away.
                    if let Ok(Some(stratz)) = source.purchases(latest_id, account as i64).await {
                        let mut keys_by_id: HashMap<i32, &str> = HashMap::new();
                        for (key, item) in constants.iter().filter(|(_, i)| i.id > 0) {
                            keys_by_id.entry(item.id).or_insert(key.as_str());
                        }
                        purchases.extend(
                            stratz.iter().filter_map(|(id, time)| keys_by_id.get(id).map(|k| (k.to_string(), *time))),
                        );
                    }
                }
            }

            purchases.sort_by_key(|p| p.1);
            for (key, time) in &purchases {
                let constant = constants.as_ref().and_then(|c| c.get(key));
                if !match_build::is_key_build_item(key, constant) {
                    continue;
                }
                items.push(MatchBuildItem {
                    name: constant.and_then(|c| c.dname.clone()).unwrap_or_else(|| key.clone()),
                    image_url: assets::item_icon(key),
                    slot: "Purchase".into(),
                    purchase_time_seconds: Some(*time),
                });
            }

            if purchases.is_empty() {
                for slot in FINAL_SLOTS {
                    let Some(item_id) = player.get(slot).and_then(Value::as_i64).filter(|id| *id > 0) else { continue };
                    let Some((key, constant)) =
                        constants.as_ref().and_then(|c| c.iter().find(|(_, i)| i64::from(i.id) == item_id))
                    else {
                        continue;
                    };
                    if !match_build::is_key_build_item(key, Some(constant)) {
                        continue;
                    }
                    items.push(MatchBuildItem {
                        name: constant.dname.clone().unwrap_or_else(|| format!("Item {item_id}")),
                        image_url: assets::item_icon(key),
                        slot: if slot.starts_with("backpack") {
                            "Backpack"
                        } else if slot == "item_neutral" {
                            "Neutral"
                        } else {
                            "Inventory"
                        }
                        .into(),
                        purchase_time_seconds: None,
                    });
                }
            }
        }

        let row = to_match_row(latest, &heroes);
        let build_label = if items.is_empty() {
            " · Item build unavailable"
        } else if items.iter().any(|i| i.slot == "Purchase") {
            " · Purchase timeline"
        } else {
            " · Final items"
        };
        items.sort_by_key(|i| i.purchase_time_seconds.unwrap_or(i32::MAX));
        let start = latest.get("start_time").and_then(as_i64).unwrap_or_default();

        PlayerMatchHistory {
            kda: row.kda.clone(),
            result: row.result.clone(),
            position,
            position_basis,
            is_position_estimated: estimated,
            played_at: (start > 0).then(|| Utc.timestamp_opt(start, 0).single()).flatten(),
            hero_match_label: format!(
                "Latest {} · {} · {} · {} · {} · {}{build_label}",
                row.hero_name,
                row.result,
                row.kda,
                format_position_label(position, estimated),
                row.duration,
                row.played_at
            ),
            hero_match_url: format!("https://www.opendota.com/matches/{latest_id}"),
            analysis_outcome,
            items,
            ..Default::default()
        }
    }

    async fn item_constants(&self) -> Option<indexmap::IndexMap<String, ItemConstant>> {
        let value = self.get_cached("build_item_constants.json", "constants/items", Some(hours(24))).await?;
        let object = value.as_object()?;
        Some(
            object
                .iter()
                .filter_map(|(key, v)| serde_json::from_value::<ItemConstant>(v.clone()).ok().map(|c| (key.clone(), c)))
                .collect(),
        )
    }

    /// Popular items for a hero (overlay build panel): display names, at most eight.
    pub async fn suggested_items(&self, hero_internal_name: &str) -> Vec<String> {
        let Ok(heroes) = self.heroes().await else { return Vec::new() };
        let Some(hero_id) = Self::resolve_hero_id(&hero_icon_key(hero_internal_name), &heroes) else {
            return Vec::new();
        };
        let Some(popularity) = self
            .get_cached(
                format!("item_popularity_{hero_id}.json"),
                format!("heroes/{hero_id}/itemPopularity"),
                Some(hours(24)),
            )
            .await
        else {
            return Vec::new();
        };
        let constants = self.item_constants().await.unwrap_or_default();
        let mut ids: Vec<i64> = Vec::new();
        for phase in ["start_game_items", "early_game_items", "mid_game_items", "late_game_items"] {
            // `{ "<item id>": <times bought> }`, most bought first.
            if let Some(items) = popularity.get(phase).and_then(Value::as_object) {
                let mut phase_items: Vec<(i64, i64)> = items
                    .iter()
                    .filter_map(|(k, v)| Some((k.parse::<i64>().ok()?, as_i64(v).unwrap_or_default())))
                    .collect();
                phase_items.sort_by_key(|(_, count)| std::cmp::Reverse(*count));
                for (id, _) in phase_items {
                    if !ids.contains(&id) {
                        ids.push(id);
                    }
                }
            }
        }
        ids.into_iter()
            .filter(|id| *id > 0)
            .take(8)
            .map(|id| {
                constants
                    .values()
                    .find(|c| i64::from(c.id) == id)
                    .and_then(|c| c.dname.clone())
                    .unwrap_or_else(|| format!("Item {id}"))
            })
            .collect()
    }
}

fn hours(n: u64) -> Duration {
    Duration::from_secs(n * 3600)
}

fn parse(text: &str) -> Option<Value> {
    if is_blank(text) {
        return None;
    }
    serde_json::from_str::<Value>(text).ok().filter(|v| !v.is_null())
}

fn is_transient(status: u16) -> bool {
    matches!(status, 408 | 429 | 502 | 503 | 504 | 522)
}

fn str_of(value: &Value, key: &str) -> String {
    value.get(key).and_then(Value::as_str).unwrap_or_default().to_string()
}

fn flexible(value: &Value) -> String {
    mango_core::json::as_flexible_string(value).unwrap_or_default()
}

fn players_of(game: &Value) -> impl Iterator<Item = &Value> {
    game.get("players").and_then(Value::as_array).into_iter().flatten()
}

fn won(m: &Value) -> bool {
    let radiant = m.get("player_slot").and_then(as_i32).unwrap_or_default() < 128;
    radiant == (m.get("radiant_win").and_then(Value::as_bool) == Some(true))
}

fn to_match_row(m: &Value, heroes: &[Hero]) -> PlayerMatchRow {
    let hero_id = m.get("hero_id").and_then(as_i32).unwrap_or_default();
    let hero = heroes.iter().find(|hero| hero.id == hero_id);
    let stat = |k: &str| m.get(k).and_then(as_i64).unwrap_or_default();
    let start = stat("start_time");
    PlayerMatchRow {
        match_id: stat("match_id").to_string(),
        hero_name: hero.map_or_else(|| format!("Hero {hero_id}"), |hero| hero.localized_name.clone()),
        hero_image_url: hero.map(|hero| assets::hero_icon(&hero_icon_key(&hero.name))).unwrap_or_default(),
        result: if won(m) { "Win" } else { "Loss" }.into(),
        analysis_outcome: None,
        kda: format!("{}/{}/{}", stat("kills"), stat("deaths"), stat("assists")),
        duration: match_build::format_duration(stat("duration")),
        played_at: Local
            .timestamp_opt(start, 0)
            .single()
            .map(|t| t.format("%-m/%-d/%Y %-I:%M %p").to_string())
            .unwrap_or_default(),
    }
}

#[cfg(test)]
mod tests;
