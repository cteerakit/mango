//! Lobby roster from the local Steam client (C# `SteamLocalRosterMapper`).
//!
//! Dota's console log names the game server for each match; the Steam client knows who is on
//! that server. Neither tells us teams or heroes, so those stay empty.

use std::sync::LazyLock;

use regex::Regex;
use serde::Deserialize;

use crate::model::MatchPlayer;
use crate::{match_id, steam_id};

static SERVER_TICKET: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r"Received Steam datagram ticket for server steamid:(\d+)[^\r\n]*\bmatch_id=(\d+)\b")
        .expect("valid regex")
});

/// Server steam id of the latest connection for `match_id` in Dota's console log.
pub fn find_server(console_text: &str, match_id: &str) -> Option<String> {
    if !match_id::is_usable(match_id) {
        return None;
    }
    SERVER_TICKET
        .captures_iter(console_text)
        .filter(|c| &c[2] == match_id)
        .last()
        .map(|c| c[1].to_string())
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct LocalRoster {
    #[serde(default)]
    server_steam_id: String,
    players: Option<Vec<LocalPlayer>>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct LocalPlayer {
    #[serde(default)]
    steam_id: String,
    #[serde(default)]
    name: String,
}

/// Players on `server_steam_id`, only if the local player is among them (2–10 players).
pub fn read_players(json: &str, server_steam_id: &str, local_steam_id: &str) -> Vec<MatchPlayer> {
    let Ok(roster) = serde_json::from_str::<LocalRoster>(json) else {
        return Vec::new();
    };
    let players = match roster.players {
        Some(players) if roster.server_steam_id == server_steam_id && (2..=10).contains(&players.len()) => players,
        _ => return Vec::new(),
    };

    let Some(local) = steam_id::to_account_id(local_steam_id) else {
        return Vec::new();
    };
    if !players.iter().any(|p| steam_id::to_account_id(&p.steam_id) == Some(local)) {
        return Vec::new();
    }

    let mut seen = Vec::new();
    players
        .into_iter()
        .filter(|p| match steam_id::to_account_id(&p.steam_id) {
            Some(account) if !seen.contains(&account) => {
                seen.push(account);
                true
            }
            _ => false,
        })
        .map(|p| MatchPlayer {
            name: if p.name.trim().is_empty() { p.steam_id.clone() } else { p.name },
            steam_id: p.steam_id,
            ..Default::default()
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    const ROSTER: &str = r#"{"serverSteamId":"901","players":[
        {"steamId":"76561197960265729","name":"Local player"},
        {"steamId":"76561197960265730","name":"Other player"}
    ]}"#;

    #[test]
    fn find_server_uses_exact_match_and_latest_connection() {
        let log = "Received Steam datagram ticket for server steamid:901 vport 0. match_id=123 mm=1\n\
                   Received Steam datagram ticket for server steamid:902 vport 0. match_id=1234 mm=1\n\
                   Received Steam datagram ticket for server steamid:903 vport 0. match_id=123 mm=1\n";
        assert_eq!(find_server(log, "123").as_deref(), Some("903"));
        assert_eq!(find_server(log, "456"), None);
        assert_eq!(find_server(log, "0"), None);
    }

    #[test]
    fn read_players_returns_current_server_members_without_inventing_teams() {
        let players = read_players(ROSTER, "901", "1");
        assert_eq!(players.len(), 2);
        assert_eq!(players[1].steam_id, "76561197960265730");
        assert_eq!(players[1].name, "Other player");
        assert!(players.iter().all(|p| p.team_name.is_empty() && p.hero_name.is_empty()));
    }

    #[test]
    fn read_players_rejects_different_server_or_missing_local_player() {
        for (server, local) in [("902", "1"), ("901", "3"), ("901", "0")] {
            assert!(read_players(ROSTER, server, local).is_empty(), "{server}/{local}");
        }
        assert!(read_players("not json", "901", "1").is_empty());
    }
}
