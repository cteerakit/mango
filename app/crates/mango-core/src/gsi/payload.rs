//! GSI payload shapes (C# `GsiPayload` and its JSON converters).
//!
//! Several nodes change shape by mode: while playing, `player`/`hero` are one object and
//! `items`/`abilities` are flat slot maps; while spectating, all of them become
//! `team2`/`team3` → `playerN` maps.

use indexmap::IndexMap;
use serde_json::Value;

use crate::json::{self, bool_of, f64_of, flexible_string_of, get, i32_of, string_of, Object};
use crate::text::contains_ignore_case;

#[derive(Debug, Clone, Default, PartialEq)]
pub struct GsiPayload {
    pub provider: Option<GsiProvider>,
    pub map: Option<GsiMap>,
    pub player: Option<GsiPlayerNode>,
    pub all_players: Option<IndexMap<String, GsiPlayer>>,
    pub hero: Option<GsiHeroNode>,
    /// Only the local player's flat slot map; `None` when spectating.
    pub items: Option<IndexMap<String, GsiItemSlot>>,
    pub abilities: Option<IndexMap<String, GsiAbilitySlot>>,
    pub draft: Option<GsiDraft>,
    pub auth: Option<GsiAuth>,
}

/// `team2`/`team3` → `player0`–`player9`.
pub type TeamMap<T> = IndexMap<String, IndexMap<String, T>>;

#[derive(Debug, Clone, Default, PartialEq)]
pub struct GsiPlayerNode {
    pub local: Option<GsiPlayer>,
    pub teams: Option<TeamMap<GsiPlayer>>,
}

impl GsiPlayerNode {
    pub fn is_spectating(&self) -> bool {
        self.teams.as_ref().is_some_and(|t| !t.is_empty())
    }
}

#[derive(Debug, Clone, Default, PartialEq)]
pub struct GsiHeroNode {
    pub local: Option<GsiHero>,
    pub teams: Option<TeamMap<GsiHero>>,
}

impl GsiHeroNode {
    pub fn is_spectating(&self) -> bool {
        self.teams.as_ref().is_some_and(|t| !t.is_empty())
    }
}

#[derive(Debug, Clone, Default, PartialEq)]
pub struct GsiProvider {
    pub name: Option<String>,
    pub app_id: i32,
    pub version: i32,
    pub timestamp: i64,
}

#[derive(Debug, Clone, Default, PartialEq)]
pub struct GsiMap {
    pub game_state: Option<String>,
    pub clock_time: i32,
    pub game_time: i32,
    pub daytime: bool,
    pub match_id: Option<String>,
    pub server_steam_id: Option<String>,
    pub radiant_score: i32,
    pub dire_score: i32,
    pub radiant_gold_adv: Option<Vec<i32>>,
    pub win_team: Option<String>,
}

#[derive(Debug, Clone, Default, PartialEq)]
pub struct GsiPlayer {
    pub steam_id: Option<String>,
    pub name: Option<String>,
    pub kills: i32,
    pub deaths: i32,
    pub assists: i32,
    pub last_hits: i32,
    pub denies: i32,
    pub gpm: i32,
    pub xpm: i32,
    pub gold: i32,
    pub gold_reliable: i32,
    pub gold_unreliable: i32,
    pub team_name: Option<String>,
    /// 2 = Radiant, 3 = Dire, 0 = unknown.
    pub team: i32,
    pub team_slot: i32,
    /// 0–4 Radiant / 128–132 Dire in many payloads; sometimes a 0–9 lobby index.
    pub player_slot: Option<i32>,
    pub hero: Option<GsiHero>,
}

#[derive(Debug, Clone, Default, PartialEq)]
pub struct GsiHero {
    pub name: Option<String>,
    pub level: i32,
    pub health: i32,
    pub max_health: i32,
    pub mana: i32,
    pub max_mana: i32,
    pub alive: bool,
}

#[derive(Debug, Clone, Default, PartialEq)]
pub struct GsiItemSlot {
    pub name: Option<String>,
    pub cooldown: f64,
    pub can_cast: bool,
}

#[derive(Debug, Clone, Default, PartialEq)]
pub struct GsiAbilitySlot {
    pub name: Option<String>,
    pub level: i32,
    pub cooldown: f64,
    pub can_cast: bool,
}

#[derive(Debug, Clone, Default, PartialEq)]
pub struct GsiDraft {
    pub active_team: i32,
    pub pick: bool,
    pub active_team_time_remaining: i32,
    pub radiant_bonus_time: i32,
    pub dire_bonus_time: i32,
    pub team2: Option<GsiDraftTeam>,
    pub team3: Option<GsiDraftTeam>,
}

#[derive(Debug, Clone, Default, PartialEq)]
pub struct GsiDraftTeam {
    pub home_team: bool,
    pub pick_ids: [i32; 5],
    pub ban_ids: [i32; 7],
}

#[derive(Debug, Clone, Default, PartialEq)]
pub struct GsiAuth {
    pub token: Option<String>,
}

impl GsiPayload {
    /// Parses a GSI POST body. Only invalid JSON or a non-object root is an error; odd field
    /// shapes fall back to defaults.
    pub fn parse(body: &str) -> Result<Self, serde_json::Error> {
        let value: Value = serde_json::from_str(body)?;
        Ok(Self::from_value(&value))
    }

    pub fn from_value(value: &Value) -> Self {
        let Some(root) = value.as_object() else {
            return Self::default();
        };
        let object = |key: &str| get(root, key).and_then(Value::as_object);

        Self {
            provider: object("provider").map(GsiProvider::from_object),
            map: object("map").map(GsiMap::from_object),
            player: object("player").map(|o| {
                if is_spectating_team_map(o) {
                    GsiPlayerNode { local: None, teams: team_map(o, GsiPlayer::from_object) }
                } else {
                    GsiPlayerNode { local: Some(GsiPlayer::from_object(o)), teams: None }
                }
            }),
            all_players: object("allplayers").and_then(all_players),
            hero: object("hero").map(|o| {
                if is_spectating_team_map(o) {
                    GsiHeroNode { local: None, teams: team_map(o, GsiHero::from_object) }
                } else {
                    GsiHeroNode { local: Some(GsiHero::from_object(o)), teams: None }
                }
            }),
            items: object("items").and_then(|o| local_slots(o, GsiItemSlot::from_object)),
            abilities: object("abilities").and_then(|o| local_slots(o, GsiAbilitySlot::from_object)),
            draft: object("draft").map(GsiDraft::from_object),
            auth: object("auth").map(|o| GsiAuth { token: string_of(o, "token") }),
        }
    }
}

impl GsiProvider {
    fn from_object(o: &Object) -> Self {
        Self {
            name: string_of(o, "name"),
            app_id: i32_of(o, "appid"),
            version: i32_of(o, "version"),
            timestamp: get(o, "timestamp").and_then(json::as_i64).unwrap_or_default(),
        }
    }
}

impl GsiMap {
    fn from_object(o: &Object) -> Self {
        Self {
            game_state: string_of(o, "game_state"),
            clock_time: i32_of(o, "clock_time"),
            game_time: i32_of(o, "game_time"),
            daytime: bool_of(o, "daytime"),
            match_id: flexible_string_of(o, "matchid"),
            server_steam_id: flexible_string_of(o, "server_steam_id"),
            radiant_score: i32_of(o, "radiant_score"),
            dire_score: i32_of(o, "dire_score"),
            radiant_gold_adv: get(o, "radiant_gold_adv")
                .and_then(Value::as_array)
                .map(|values| values.iter().map(|v| json::as_i32(v).unwrap_or_default()).collect()),
            win_team: string_of(o, "win_team"),
        }
    }
}

impl GsiPlayer {
    pub(crate) fn from_object(o: &Object) -> Self {
        Self {
            steam_id: string_of(o, "steamid"),
            name: string_of(o, "name"),
            kills: i32_of(o, "kills"),
            deaths: i32_of(o, "deaths"),
            assists: i32_of(o, "assists"),
            last_hits: i32_of(o, "last_hits"),
            denies: i32_of(o, "denies"),
            gpm: i32_of(o, "gpm"),
            xpm: i32_of(o, "xpm"),
            gold: i32_of(o, "gold"),
            gold_reliable: i32_of(o, "gold_reliable"),
            gold_unreliable: i32_of(o, "gold_unreliable"),
            team_name: string_of(o, "team_name"),
            team: get(o, "team").map(team_number).unwrap_or_default(),
            team_slot: i32_of(o, "team_slot"),
            player_slot: get(o, "player_slot").and_then(json::as_i32),
            hero: get(o, "hero").and_then(Value::as_object).map(GsiHero::from_object),
        }
    }

    fn looks_like_player(&self) -> bool {
        let present = |v: &Option<String>| v.as_deref().is_some_and(|s| !s.trim().is_empty());
        present(&self.steam_id)
            || present(&self.name)
            || present(&self.team_name)
            || matches!(self.team, 2 | 3)
            || self.player_slot.is_some()
    }
}

impl GsiHero {
    fn from_object(o: &Object) -> Self {
        Self {
            name: string_of(o, "name"),
            level: i32_of(o, "level"),
            health: i32_of(o, "health"),
            max_health: i32_of(o, "max_health"),
            mana: i32_of(o, "mana"),
            max_mana: i32_of(o, "max_mana"),
            alive: bool_of(o, "alive"),
        }
    }
}

impl GsiItemSlot {
    fn from_object(o: &Object) -> Self {
        Self { name: string_of(o, "name"), cooldown: f64_of(o, "cooldown"), can_cast: bool_of(o, "can_cast") }
    }
}

impl GsiAbilitySlot {
    fn from_object(o: &Object) -> Self {
        Self {
            name: string_of(o, "name"),
            level: i32_of(o, "level"),
            cooldown: f64_of(o, "cooldown"),
            can_cast: bool_of(o, "can_cast"),
        }
    }
}

impl GsiDraft {
    fn from_object(o: &Object) -> Self {
        let team = |key: &str| get(o, key).and_then(Value::as_object).map(GsiDraftTeam::from_object);
        Self {
            active_team: i32_of(o, "activeteam"),
            pick: bool_of(o, "pick"),
            active_team_time_remaining: i32_of(o, "activeteam_time_remaining"),
            radiant_bonus_time: i32_of(o, "radiant_bonus_time"),
            dire_bonus_time: i32_of(o, "dire_bonus_time"),
            team2: team("team2"),
            team3: team("team3"),
        }
    }
}

impl GsiDraftTeam {
    fn from_object(o: &Object) -> Self {
        Self {
            home_team: bool_of(o, "home_team"),
            pick_ids: std::array::from_fn(|i| i32_of(o, &format!("pick{i}_id"))),
            ban_ids: std::array::from_fn(|i| i32_of(o, &format!("ban{i}_id"))),
        }
    }
}

/// GSI `team` is a number, a numeric string, or a name containing radiant/dire.
fn team_number(value: &Value) -> i32 {
    if let Some(n) = json::as_i32(value) {
        return n;
    }
    match value.as_str() {
        Some(s) if contains_ignore_case(s, "radiant") => 2,
        Some(s) if contains_ignore_case(s, "dire") => 3,
        _ => 0,
    }
}

/// Spectating uses `team2`/`team3` object buckets, not local fields like `team_name`.
fn is_spectating_team_map(o: &Object) -> bool {
    o.iter().any(|(key, value)| is_team_bucket_key(key) && value.is_object())
}

fn is_team_bucket_key(name: &str) -> bool {
    name.len() > 4
        && name.get(..4).is_some_and(|head| head.eq_ignore_ascii_case("team"))
        && name[4..].chars().all(|c| c.is_ascii_digit())
}

fn team_map<T>(o: &Object, read: impl Fn(&Object) -> T) -> Option<TeamMap<T>> {
    let mut teams = TeamMap::new();
    for (team, value) in o {
        let Some(slots) = value.as_object().filter(|_| is_team_bucket_key(team)) else {
            continue;
        };
        let players: IndexMap<_, _> = slots
            .iter()
            .filter_map(|(key, v)| v.as_object().map(|p| (key.clone(), read(p))))
            .collect();
        if !players.is_empty() {
            teams.insert(team.clone(), players);
        }
    }
    (!teams.is_empty()).then_some(teams)
}

/// Playing sends flat slot maps; spectators send team → player → slots, which is dropped.
fn local_slots<T>(o: &Object, read: impl Fn(&Object) -> T) -> Option<IndexMap<String, T>> {
    if is_spectating_team_map(o) {
        return None;
    }
    let slots: IndexMap<_, _> =
        o.iter().filter_map(|(key, v)| v.as_object().map(|s| (key.clone(), read(s)))).collect();
    (!slots.is_empty()).then_some(slots)
}

/// Spectator team maps flatten to `team2.player0` keys; otherwise keep player-like entries.
fn all_players(o: &Object) -> Option<IndexMap<String, GsiPlayer>> {
    let mut result = IndexMap::new();
    if is_spectating_team_map(o) {
        for (team, players) in team_map(o, GsiPlayer::from_object)? {
            for (slot, player) in players {
                result.insert(format!("{team}.{slot}"), player);
            }
        }
    } else {
        for (key, value) in o {
            if let Some(player) = value.as_object().map(GsiPlayer::from_object) {
                if player.looks_like_player() {
                    result.insert(key.clone(), player);
                }
            }
        }
    }
    (!result.is_empty()).then_some(result)
}
