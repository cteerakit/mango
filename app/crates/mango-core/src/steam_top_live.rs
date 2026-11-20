//! Live game lookup from Steam's `GetTopLiveGame` (C# `SteamTopLiveMapper`).

use std::collections::HashMap;

use serde_json::Value;

use crate::json::{self, get_ci, Object};
use crate::match_id;
use crate::model::MatchPlayer;
use crate::text::eq_ignore_case;

#[derive(Debug, Clone, Default, PartialEq)]
pub struct SteamTopLiveHit {
    pub server_steam_id: String,
    pub match_id: String,
    pub players: Vec<MatchPlayer>,
}

#[derive(Debug, Clone, Default, PartialEq)]
pub struct SteamTopLiveGame {
    pub server_steam_id: Option<String>,
    pub match_id: Option<String>,
    pub players: Option<Vec<SteamTopLivePlayer>>,
}

#[derive(Debug, Clone, Default, PartialEq)]
pub struct SteamTopLivePlayer {
    pub account_id: u64,
    pub hero_id: i32,
}

/// The `game_list`, at the top level or inside one `result` wrapper.
pub fn parse_games(body: &str) -> Result<Vec<SteamTopLiveGame>, serde_json::Error> {
    let value: Value = serde_json::from_str(body)?;
    Ok(games_from_value(&value))
}

fn games_from_value(value: &Value) -> Vec<SteamTopLiveGame> {
    let Some(root) = value.as_object() else { return Vec::new() };
    let list = |o: &Object| get_ci(o, "game_list").and_then(Value::as_array).cloned();
    let games = list(root)
        .or_else(|| get_ci(root, "result").and_then(Value::as_object).and_then(list))
        .unwrap_or_default();
    games.iter().filter_map(Value::as_object).map(game_from_object).collect()
}

fn game_from_object(o: &Object) -> SteamTopLiveGame {
    SteamTopLiveGame {
        server_steam_id: get_ci(o, "server_steam_id").and_then(json::as_flexible_string),
        match_id: get_ci(o, "match_id").and_then(json::as_flexible_string),
        players: get_ci(o, "players").and_then(Value::as_array).map(|players| {
            players
                .iter()
                .filter_map(Value::as_object)
                .map(|p| SteamTopLivePlayer {
                    account_id: get_ci(p, "account_id").and_then(json::as_u64).unwrap_or_default(),
                    hero_id: get_ci(p, "hero_id").and_then(json::as_i32).unwrap_or_default(),
                })
                .collect()
        }),
    }
}

/// The game with `match_id` when both ids are known, otherwise the one containing `account_id`.
pub fn find_game(
    games: &[SteamTopLiveGame],
    account_id: Option<u64>,
    match_id: Option<&str>,
    hero_names: Option<&HashMap<i32, String>>,
) -> Option<SteamTopLiveHit> {
    games.iter().find(|game| matches(game, account_id, match_id)).map(|game| to_hit(game, hero_names))
}

pub fn to_hit(game: &SteamTopLiveGame, hero_names: Option<&HashMap<i32, String>>) -> SteamTopLiveHit {
    SteamTopLiveHit {
        server_steam_id: game.server_steam_id.as_deref().unwrap_or_default().trim().to_string(),
        match_id: game.match_id.as_deref().unwrap_or_default().trim().to_string(),
        players: to_players(game, hero_names),
    }
}

/// The live list carries no team data and its order is not team order, so sides stay unknown.
pub fn to_players(game: &SteamTopLiveGame, hero_names: Option<&HashMap<i32, String>>) -> Vec<MatchPlayer> {
    game.players
        .iter()
        .flatten()
        .filter(|p| p.account_id != 0)
        .map(|p| MatchPlayer {
            steam_id: p.account_id.to_string(),
            name: p.account_id.to_string(),
            hero_name: hero_names.and_then(|names| names.get(&p.hero_id)).cloned().unwrap_or_default(),
            ..Default::default()
        })
        .collect()
}

fn matches(game: &SteamTopLiveGame, account_id: Option<u64>, match_id: Option<&str>) -> bool {
    let game_match = game.match_id.as_deref().unwrap_or_default();
    if let Some(wanted) = match_id.filter(|id| match_id::is_usable(id)) {
        if match_id::is_usable(game_match) {
            return eq_ignore_case(game_match, wanted);
        }
    }
    match account_id {
        Some(account) if account > 0 => {
            game.players.iter().flatten().any(|player| player.account_id == account)
        }
        _ => false,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn find_game_rejects_same_account_in_different_known_match() {
        let games = [SteamTopLiveGame {
            match_id: Some("111".into()),
            players: Some(vec![SteamTopLivePlayer { account_id: 42, hero_id: 0 }]),
            ..Default::default()
        }];
        assert!(find_game(&games, Some(42), Some("222"), None).is_none());
    }

    #[test]
    fn find_game_matches_local_account_and_maps_ten_players() {
        let json = r#"{ "game_list": [ {
            "server_steam_id": 90153324321222656,
            "match_id": 8345123456,
            "players": [
              { "account_id": 1088417288, "hero_id": 14 }, { "account_id": 2, "hero_id": 1 },
              { "account_id": 3, "hero_id": 2 }, { "account_id": 4, "hero_id": 3 },
              { "account_id": 5, "hero_id": 4 }, { "account_id": 6, "hero_id": 5 },
              { "account_id": 7, "hero_id": 6 }, { "account_id": 8, "hero_id": 7 },
              { "account_id": 9, "hero_id": 8 }, { "account_id": 10, "hero_id": 9 }
            ] } ] }"#;
        let games = parse_games(json).unwrap();
        let hero_names = HashMap::from([(14, "npc_dota_hero_pudge".to_string())]);

        let hit = find_game(&games, Some(1_088_417_288), Some("8345123456"), Some(&hero_names)).unwrap();

        assert_eq!(hit.server_steam_id, "90153324321222656");
        assert_eq!(hit.match_id, "8345123456");
        assert_eq!(hit.players.len(), 10);
        assert!(hit.players.iter().all(|p| p.team_name.is_empty()));
        assert_eq!(hit.players[0].hero_name, "npc_dota_hero_pudge");
        assert_eq!(hit.players[0].steam_id, "1088417288");
    }

    #[test]
    fn find_game_reads_nested_result_wrapper() {
        let json = r#"{ "result": { "game_list": [ {
            "server_steam_id": "9012", "match_id": "1", "players": [ { "account_id": 42, "hero_id": 1 } ] } ] } }"#;
        let hit = find_game(&parse_games(json).unwrap(), Some(42), None, None).unwrap();
        assert_eq!(hit.server_steam_id, "9012");
        assert_eq!(hit.players.len(), 1);
    }

    #[test]
    fn find_game_returns_none_when_account_missing() {
        let json = r#"{ "game_list": [ { "players": [ { "account_id": 1, "hero_id": 1 } ] } ] }"#;
        assert!(find_game(&parse_games(json).unwrap(), Some(99), None, None).is_none());
    }
}
