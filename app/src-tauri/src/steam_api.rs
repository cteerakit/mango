//! Steam Web API live endpoints, the only source of teams for ordinary matches
//! (C# `SteamWebApiService`). Requires the user's own key.

use std::collections::HashMap;

use futures::stream::{FuturesUnordered, StreamExt};
use mango_core::json::{as_i32, as_u64};
use mango_core::match_id;
use mango_core::model::MatchPlayer;
use mango_core::steam_top_live::{self, SteamTopLiveHit};
use serde_json::Value;

use crate::http::{Request, SharedHttp};

pub struct SteamApi {
    http: SharedHttp,
}

fn encode(value: &str) -> String {
    value
        .bytes()
        .map(|b| match b {
            b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9' | b'-' | b'_' | b'.' | b'~' => (b as char).to_string(),
            _ => format!("%{b:02X}"),
        })
        .collect()
}

impl SteamApi {
    pub fn new(http: SharedHttp) -> Self {
        Self { http }
    }

    /// Both teams of the game on `server_steam_id`, with heroes and stats.
    pub async fn realtime_roster(
        &self,
        server_steam_id: &str,
        api_key: &str,
        hero_names: &HashMap<i32, String>,
        expected_match_id: &str,
    ) -> Option<Vec<MatchPlayer>> {
        if server_steam_id.trim().is_empty() || api_key.trim().is_empty() {
            return None;
        }
        let url = format!(
            "https://api.steampowered.com/IDOTA2MatchStats_570/GetRealtimeStats/v1/?key={}&server_steam_id={}",
            encode(api_key.trim()),
            encode(server_steam_id.trim())
        );
        let response = self.http.send(Request::get(url)).await.ok()?;
        if !response.is_success() {
            return None;
        }
        let payload: Value = serde_json::from_slice(&response.body).ok()?;
        parse_realtime(&payload, hero_names, expected_match_id)
    }

    /// Searches every `GetTopLiveGame` partner list in parallel; the first hit wins.
    pub async fn find_top_live_game(
        &self,
        account_id: Option<u64>,
        match_id_text: &str,
        api_key: &str,
        hero_names: &HashMap<i32, String>,
    ) -> Option<SteamTopLiveHit> {
        if api_key.trim().is_empty() || (account_id.is_none() && !match_id::is_usable(match_id_text)) {
            return None;
        }
        let key = encode(api_key.trim());
        let mut partners: FuturesUnordered<_> = (0..11)
            .map(|partner| {
                let url = format!("https://api.steampowered.com/IDOTA2Match_570/GetTopLiveGame/v1/?key={key}&partner={partner}");
                let http = self.http.clone();
                async move {
                    let response = http.send(Request::get(url)).await.ok()?;
                    if !response.is_success() {
                        return None;
                    }
                    steam_top_live::parse_games(&response.text()).ok()
                }
            })
            .collect();
        while let Some(games) = partners.next().await {
            if let Some(hit) = games.and_then(|games| {
                steam_top_live::find_game(&games, account_id, Some(match_id_text), Some(hero_names))
            }) {
                return Some(hit);
            }
        }
        None
    }
}

fn parse_realtime(payload: &Value, hero_names: &HashMap<i32, String>, expected_match_id: &str) -> Option<Vec<MatchPlayer>> {
    let root = if payload.get("teams").is_some() || payload.get("match").is_some() {
        payload
    } else {
        payload.get("result").unwrap_or(payload)
    };
    let returned = root.pointer("/match/match_id").and_then(mango_core::json::as_flexible_string).unwrap_or_default();
    if match_id::is_usable(expected_match_id) && match_id::is_usable(&returned) && expected_match_id != returned {
        return None;
    }
    let teams = root.get("teams").and_then(Value::as_array).filter(|t| !t.is_empty())?;

    let mut players = Vec::new();
    for team in teams {
        let team_name = if team.get("team_number").and_then(as_i32) == Some(3) { "dire" } else { "radiant" };
        for (index, p) in team.get("players").and_then(Value::as_array).into_iter().flatten().enumerate() {
            let account = p.get("accountid").and_then(as_u64).unwrap_or_default();
            let stat = |k: &str| p.get(k).and_then(as_i32).unwrap_or_default();
            let name = p.get("name").and_then(Value::as_str).map(str::trim).filter(|n| !n.is_empty());
            players.push(MatchPlayer {
                steam_id: if account > 0 { account.to_string() } else { String::new() },
                name: name.map(str::to_string).unwrap_or_else(|| {
                    if account > 0 { account.to_string() } else { "Unknown".into() }
                }),
                team_name: team_name.into(),
                team_slot: index as i32,
                hero_name: hero_names.get(&stat("heroid")).cloned().unwrap_or_default(),
                hero_level: stat("level"),
                kills: stat("kill_count"),
                deaths: stat("death_count"),
                assists: stat("assists_count"),
                last_hits: stat("lh_count"),
                denies: stat("denies_count"),
                gold: stat("gold"),
                ..Default::default()
            });
        }
    }
    (!players.is_empty()).then_some(players)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::http::fake::FakeHttp;
    use serde_json::json;

    #[tokio::test]
    async fn realtime_rejects_different_known_match() {
        let http = FakeHttp::ok_or_404(|_| {
            Some(r#"{"match":{"match_id":111},"teams":[{"team_number":2,"players":[{"accountid":42}]}]}"#.into())
        });
        let api = SteamApi::new(http);
        assert!(api.realtime_roster("901", "test-key", &HashMap::new(), "222").await.is_none());
    }

    #[tokio::test]
    async fn realtime_reads_all_players_and_stats() {
        for wrapped in [false, true] {
            let teams: Vec<Value> = [2, 3]
                .iter()
                .map(|team| {
                    json!({"team_number": team, "players": (0..5).map(|slot| json!({
                        "accountid": team * 10 + slot, "name": format!("Player {team}-{slot}"), "heroid": 14,
                        "level": 12, "kill_count": 3, "death_count": 2, "assists_count": 7,
                        "lh_count": 84, "denies_count": 6, "gold": 1200
                    })).collect::<Vec<_>>()})
                })
                .collect();
            let mut payload = json!({"match": {"match_id": "123"}, "teams": teams});
            if wrapped {
                payload = json!({ "result": payload });
            }
            let body = payload.to_string();
            let http = FakeHttp::ok_or_404(move |r| {
                assert!(r.url.contains("server_steam_id=90295161133586447") && r.url.contains("key=test-key"));
                Some(body.clone())
            });
            let heroes = HashMap::from([(14, "npc_dota_hero_pudge".to_string())]);

            let players = SteamApi::new(http).realtime_roster("90295161133586447", "test-key", &heroes, "123").await.unwrap();

            assert_eq!(players.len(), 10);
            assert_eq!(players[0].team_name, "radiant");
            assert_eq!(players[5].team_name, "dire");
            assert_eq!(players[0].steam_id, "20");
            assert_eq!(players[0].hero_name, "npc_dota_hero_pudge");
            assert_eq!(players[0].kda(), "3/2/7");
            assert_eq!((players[0].last_hits, players[0].denies), (84, 6));
        }
    }

    #[tokio::test]
    async fn top_live_searches_partners_until_a_hit() {
        let http = FakeHttp::ok_or_404(|r| {
            Some(if r.url.ends_with("partner=7") {
                r#"{"game_list":[{"match_id":"55","server_steam_id":"9","players":[{"account_id":42,"hero_id":1}]}]}"#.into()
            } else {
                r#"{"game_list":[]}"#.into()
            })
        });
        let hit = SteamApi::new(http.clone()).find_top_live_game(Some(42), "55", "k", &HashMap::new()).await.unwrap();
        assert_eq!(hit.server_steam_id, "9");
        assert!(SteamApi::new(http).find_top_live_game(None, "0", "k", &HashMap::new()).await.is_none());
    }

    #[test]
    fn url_encoding() {
        assert_eq!(encode("a b&c"), "a%20b%26c");
    }
}
