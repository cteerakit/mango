//! Pure helpers for a player's latest hero match (from C# `OpenDotaService.MatchHistory`).

use serde::Deserialize;
use serde_json::Value;

/// Items left out of builds even though they pass the cost rule (e.g. Oblivion Staff).
const HIDDEN_BUILD_ITEMS: &[&str] = &["oblivion_staff"];
/// Cheap or component-less items that still matter in a build.
const ALWAYS_KEY_ITEMS: &[&str] = &["blink", "ghost", "gem", "ultimate_scepter_2", "aghanims_shard"];

/// An entry of OpenDota `constants/items`.
#[derive(Debug, Clone, Default, PartialEq, Deserialize)]
#[serde(default)]
pub struct ItemConstant {
    pub id: i32,
    pub dname: Option<String>,
    pub cost: Option<i32>,
    pub components: Option<Vec<Option<String>>>,
}

/// Completed items worth at least 1000 gold, plus a few key cheap ones; no recipes.
pub fn is_key_build_item(key: &str, item: Option<&ItemConstant>) -> bool {
    if HIDDEN_BUILD_ITEMS.contains(&key) {
        return false;
    }
    if ALWAYS_KEY_ITEMS.contains(&key) {
        return true;
    }
    !key.starts_with("recipe_")
        && item.is_some_and(|i| i.cost.unwrap_or(0) >= 1000 && i.components.as_ref().is_some_and(|c| !c.is_empty()))
}

/// An `i32` field, only when it is a JSON integer.
pub fn read_int(row: &Value, property: &str) -> Option<i32> {
    row.get(property).and_then(Value::as_i64).and_then(|n| i32::try_from(n).ok())
}

/// Guesses position 1–5 for an unparsed match: the top three earners are cores (1–3), the
/// rest supports (4–5), using lane roles to place players within each group when available.
pub fn estimate_position_from_farm(players: &[Value], player: &Value) -> Option<i32> {
    let slot = read_int(player, "player_slot")?;
    let mut team: Vec<&Value> = players
        .iter()
        .filter(|row| read_int(row, "player_slot").is_some_and(|other| (other < 128) == (slot < 128)))
        .collect();

    let mut slots: Vec<i32> = team.iter().filter_map(|row| read_int(row, "player_slot")).collect();
    slots.sort_unstable();
    slots.dedup();
    let farm_known = team.iter().all(|row| {
        read_int(row, "gold_per_min").is_some_and(|g| g > 0) && read_int(row, "last_hits").is_some_and(|l| l >= 0)
    });
    if team.len() != 5 || slots.len() != 5 || !farm_known {
        return None;
    }

    // Stable, like LINQ OrderByDescending().ThenByDescending().
    team.sort_by(|a, b| {
        read_int(b, "gold_per_min")
            .cmp(&read_int(a, "gold_per_min"))
            .then(read_int(b, "last_hits").cmp(&read_int(a, "last_hits")))
    });

    let mut assignments: Vec<(i32, i32)> = Vec::new();
    for (group, positions) in [(&team[..3], &[1, 2, 3][..]), (&team[3..], &[4, 5][..])] {
        let mut remaining = positions.to_vec();
        let mut unresolved = Vec::new();
        for row in group {
            let lane = read_int(row, "lane_role");
            let preferred = if positions[0] == 1 {
                lane
            } else {
                match lane {
                    Some(1) => Some(5),
                    Some(2 | 3) => Some(4),
                    _ => None,
                }
            };
            let row_slot = read_int(row, "player_slot").expect("checked above");
            match preferred.and_then(|p| remaining.iter().position(|r| *r == p)) {
                Some(index) => assignments.push((row_slot, remaining.remove(index))),
                None => unresolved.push(row_slot),
            }
        }
        for (row_slot, position) in unresolved.into_iter().zip(remaining) {
            assignments.push((row_slot, position));
        }
    }
    assignments.into_iter().find(|(s, _)| *s == slot).map(|(_, p)| p)
}

/// `31:05`, or `1:02:03` past an hour; `—` when unknown.
pub fn format_duration(seconds: i64) -> String {
    if seconds <= 0 {
        return "—".into();
    }
    let (h, m, s) = (seconds / 3600, seconds % 3600 / 60, seconds % 60);
    if h >= 1 {
        format!("{h}:{m:02}:{s:02}")
    } else {
        format!("{m}:{s:02}")
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    fn dire_team(lane_for: Option<(i64, i64)>) -> Vec<Value> {
        let rows = [(456, 128, 650, 350), (457, 129, 600, 300), (458, 130, 500, 200), (459, 131, 350, 60), (460, 132, 280, 20)];
        let mut players: Vec<Value> = rows
            .iter()
            .map(|(id, slot, gpm, lh)| json!({"account_id": id, "player_slot": slot, "gold_per_min": gpm, "last_hits": lh}))
            .collect();
        if let Some((id, lane)) = lane_for {
            for p in &mut players {
                if p["account_id"] == id {
                    p["lane_role"] = json!(lane);
                }
            }
        }
        players.push(json!({"account_id": 777, "player_slot": 0, "gold_per_min": 900, "last_hits": 500}));
        players
    }

    fn position_of(players: &[Value], account: i64) -> Option<i32> {
        let player = players.iter().find(|p| p["account_id"] == account).unwrap();
        estimate_position_from_farm(players, player)
    }

    #[test]
    fn unparsed_match_estimates_position_from_own_team_farm() {
        let plain = dire_team(None);
        for (account, expected) in [(456, 1), (457, 2), (458, 3), (459, 4), (460, 5)] {
            assert_eq!(position_of(&plain, account), Some(expected), "{account}");
        }
        assert_eq!(position_of(&dire_team(Some((456, 3))), 456), Some(3));
        assert_eq!(position_of(&dire_team(Some((458, 1))), 458), Some(1));
        assert_eq!(position_of(&dire_team(Some((459, 1))), 459), Some(5));
        assert_eq!(position_of(&dire_team(Some((460, 3))), 460), Some(4));
    }

    #[test]
    fn incomplete_teams_are_not_estimated() {
        let players = vec![json!({"player_slot": 0, "gold_per_min": 500, "last_hits": 10})];
        assert_eq!(estimate_position_from_farm(&players, &players[0]), None);
        let no_slot = json!({"gold_per_min": 1});
        assert_eq!(estimate_position_from_farm(std::slice::from_ref(&no_slot), &no_slot), None);
    }

    #[test]
    fn key_items() {
        let bkb = ItemConstant { cost: Some(4050), components: Some(vec![Some("ogre_axe".into())]), ..Default::default() };
        let wand = ItemConstant { cost: Some(450), components: Some(vec![Some("branches".into())]), ..Default::default() };
        let hammer = ItemConstant { cost: Some(1600), ..Default::default() };
        assert!(is_key_build_item("black_king_bar", Some(&bkb)));
        assert!(!is_key_build_item("magic_wand", Some(&wand)));
        assert!(!is_key_build_item("mithril_hammer", Some(&hammer)));
        assert!(!is_key_build_item("recipe_black_king_bar", Some(&bkb)));
        assert!(!is_key_build_item("oblivion_staff", Some(&bkb)));
        assert!(is_key_build_item("blink", None));
    }

    #[test]
    fn durations() {
        assert_eq!(format_duration(0), "—");
        assert_eq!(format_duration(1800), "30:00");
        assert_eq!(format_duration(3723), "1:02:03");
    }
}
