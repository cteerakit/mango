//! Match data sent to the UI (C# `MatchSnapshot`, `MatchPlayer`).
//!
//! Only data and the derived values that logic depends on live here. Pure display strings
//! (tooltips, glyphs, button labels) belong in the TypeScript UI.

use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};

use crate::history::PlayerMatchHistory;
use crate::text::{contains_ignore_case, is_blank, replace_ignore_ascii_case};
use crate::{ranks, steam_id, timers};

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct MatchSnapshot {
    pub received_at_utc: DateTime<Utc>,
    pub is_connected: bool,
    pub game_state: String,
    pub clock_time_seconds: i32,
    pub game_time_seconds: i32,
    pub is_daytime: bool,
    pub match_id: String,
    pub server_steam_id: String,
    pub radiant_score: i32,
    pub dire_score: i32,
    pub net_worth_lead: i32,
    pub player_name: String,
    pub steam_id: String,
    pub kills: i32,
    pub deaths: i32,
    pub assists: i32,
    pub last_hits: i32,
    pub denies: i32,
    pub gpm: i32,
    pub xpm: i32,
    pub gold: i32,
    pub team_name: String,
    pub hero_name: String,
    pub hero_level: i32,
    pub health: i32,
    pub max_health: i32,
    pub mana: i32,
    pub max_mana: i32,
    pub hero_alive: bool,
    pub items: Vec<SlotSnapshot>,
    pub abilities: Vec<SlotSnapshot>,
    pub players: Vec<MatchPlayer>,
    pub draft: DraftSnapshot,
}

impl Default for MatchSnapshot {
    fn default() -> Self {
        Self {
            received_at_utc: DateTime::<Utc>::UNIX_EPOCH,
            is_connected: false,
            game_state: String::new(),
            clock_time_seconds: 0,
            game_time_seconds: 0,
            is_daytime: false,
            match_id: String::new(),
            server_steam_id: String::new(),
            radiant_score: 0,
            dire_score: 0,
            net_worth_lead: 0,
            player_name: String::new(),
            steam_id: String::new(),
            kills: 0,
            deaths: 0,
            assists: 0,
            last_hits: 0,
            denies: 0,
            gpm: 0,
            xpm: 0,
            gold: 0,
            team_name: String::new(),
            hero_name: String::new(),
            hero_level: 0,
            health: 0,
            max_health: 0,
            mana: 0,
            max_mana: 0,
            hero_alive: false,
            items: Vec::new(),
            abilities: Vec::new(),
            players: Vec::new(),
            draft: DraftSnapshot::default(),
        }
    }
}

impl MatchSnapshot {
    pub fn formatted_clock(&self) -> String {
        timers::format_clock(self.clock_time_seconds)
    }

    pub fn kda(&self) -> String {
        format!("{}/{}/{}", self.kills, self.deaths, self.assists)
    }

    pub fn health_percent(&self) -> f64 {
        if self.max_health > 0 {
            f64::from(self.health) / f64::from(self.max_health)
        } else {
            0.0
        }
    }

    pub fn mana_percent(&self) -> f64 {
        if self.max_mana > 0 {
            f64::from(self.mana) / f64::from(self.max_mana)
        } else {
            0.0
        }
    }

    pub fn is_in_draft(&self) -> bool {
        contains_ignore_case(&self.game_state, "HERO_SELECTION")
            || contains_ignore_case(&self.game_state, "STRATEGY")
    }

    pub fn is_in_game(&self) -> bool {
        contains_ignore_case(&self.game_state, "GAME_IN_PROGRESS")
    }

    /// Draft through the end of the game, including loading, showcase and pre-horn.
    pub fn is_in_match(&self) -> bool {
        self.is_in_draft()
            || self.is_in_game()
            || matches!(
                self.game_state.as_str(),
                "DOTA_GAMERULES_STATE_WAIT_FOR_PLAYERS_TO_LOAD"
                    | "DOTA_GAMERULES_STATE_TEAM_SHOWCASE"
                    | "DOTA_GAMERULES_STATE_PRE_GAME"
            )
    }
}

#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct SlotSnapshot {
    pub slot: String,
    pub name: String,
    pub level: i32,
    pub cooldown: f64,
    pub can_cast: bool,
}

#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct DraftSnapshot {
    pub is_active: bool,
    pub active_team: i32,
    pub is_pick_phase: bool,
    pub time_remaining: i32,
    pub player_is_radiant: bool,
    pub radiant_hero_ids: Vec<i32>,
    pub dire_hero_ids: Vec<i32>,
}

impl DraftSnapshot {
    pub fn enemy_hero_ids(&self) -> &[i32] {
        if self.player_is_radiant {
            &self.dire_hero_ids
        } else {
            &self.radiant_hero_ids
        }
    }

    pub fn ally_hero_ids(&self) -> &[i32] {
        if self.player_is_radiant {
            &self.radiant_hero_ids
        } else {
            &self.dire_hero_ids
        }
    }
}

#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct MatchPlayer {
    pub steam_id: String,
    pub name: String,
    pub team_name: String,
    pub team_slot: i32,
    /// Raw GSI/OpenDota hero name, e.g. `npc_dota_hero_axe`.
    pub hero_name: String,
    pub hero_level: i32,
    pub kills: i32,
    pub deaths: i32,
    pub assists: i32,
    pub last_hits: i32,
    pub denies: i32,
    pub gpm: i32,
    pub xpm: i32,
    pub gold: i32,

    pub note: String,
    pub like_count: i32,
    pub dislike_count: i32,
    pub is_toxic: bool,
    pub can_vote: bool,
    pub vote_unavailable_reason: String,
    /// `like`, `dislike` or empty.
    pub current_match_vote: String,

    pub avatar_url: String,
    pub hero_image_url: String,
    pub rank_icon_url: String,
    pub rank_star_url: String,
    pub rank_label: String,
    /// True when the medal is inferred (match history or lobby average) rather than confirmed or set manually.
    pub rank_is_estimated: bool,
    pub reported_rank_tier: Option<i32>,
    pub matches_label: String,
    pub possible_smurf: bool,
    pub is_profile_private: bool,
    pub is_local_player: bool,
    pub history: PlayerMatchHistory,
}

impl MatchPlayer {
    pub fn has_identity(&self) -> bool {
        !is_blank(&self.steam_id) || !is_blank(&self.name)
    }

    /// `axe` for `npc_dota_hero_axe`; the key hero images and lookups use.
    pub fn hero_icon_key(&self) -> String {
        hero_icon_key(&self.hero_name)
    }

    pub fn display_hero_name(&self) -> String {
        self.hero_icon_key().replace('_', " ")
    }

    pub fn kda(&self) -> String {
        format!("{}/{}/{}", self.kills, self.deaths, self.assists)
    }

    pub fn can_mark_toxic(&self) -> bool {
        !is_blank(&self.steam_id)
    }

    pub fn can_edit_rank(&self) -> bool {
        ranks::can_override(self.is_profile_private, self.reported_rank_tier)
            && steam_id::to_account_id(&self.steam_id).is_some()
    }
}

pub fn hero_icon_key(hero_name: &str) -> String {
    let key = hero_name.trim();
    if key.is_empty() {
        return String::new();
    }
    const PREFIX: &str = "npc_dota_hero_";
    let key = match key.get(..PREFIX.len()) {
        Some(head) if head.eq_ignore_ascii_case(PREFIX) => &key[PREFIX.len()..],
        _ => key,
    };
    key.replace(' ', "_").to_lowercase()
}

/// Strips Valve's internal prefixes: `npc_dota_hero_`, `item_`, `ability_`.
pub fn format_internal_name(internal_name: &str) -> String {
    if is_blank(internal_name) {
        return String::new();
    }
    let name = replace_ignore_ascii_case(internal_name, "npc_dota_hero_", "");
    let name = replace_ignore_ascii_case(&name, "item_", "");
    replace_ignore_ascii_case(&name, "ability_", "")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn is_in_match_covers_draft_through_game() {
        for (state, expected) in [
            ("DOTA_GAMERULES_STATE_HERO_SELECTION", true),
            ("DOTA_GAMERULES_STATE_STRATEGY_TIME", true),
            ("DOTA_GAMERULES_STATE_WAIT_FOR_PLAYERS_TO_LOAD", true),
            ("DOTA_GAMERULES_STATE_TEAM_SHOWCASE", true),
            ("DOTA_GAMERULES_STATE_PRE_GAME", true),
            ("DOTA_GAMERULES_STATE_GAME_IN_PROGRESS", true),
            ("DOTA_GAMERULES_STATE_POST_GAME", false),
            ("", false),
        ] {
            let snapshot = MatchSnapshot { game_state: state.into(), ..Default::default() };
            assert_eq!(snapshot.is_in_match(), expected, "{state}");
        }
    }

    #[test]
    fn hero_keys_strip_prefix_and_normalize() {
        assert_eq!(hero_icon_key("npc_dota_hero_Shadow Shaman"), "shadow_shaman");
        assert_eq!(hero_icon_key("axe"), "axe");
        assert_eq!(hero_icon_key("  "), "");
        let player = MatchPlayer { hero_name: "npc_dota_hero_queenofpain".into(), ..Default::default() };
        assert_eq!(player.display_hero_name(), "queenofpain");
    }

    #[test]
    fn camel_case_json_for_the_ui() {
        let json = serde_json::to_value(MatchPlayer { steam_id: "1".into(), ..Default::default() }).unwrap();
        assert_eq!(json["steamId"], "1");
        assert!(json.get("history").is_some());
    }
}
