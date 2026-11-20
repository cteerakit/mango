//! Combining GSI's roster with external rosters (C# `MatchRosterMerger`).
//!
//! Playing GSI usually knows only the local player; Steam and OpenDota fill in the lobby.

use std::cmp::Ordering;

use crate::model::MatchPlayer;
use crate::text::{cmp_ignore_case, contains_ignore_case, eq_ignore_case, first_non_empty, is_blank};
use crate::{match_id, steam_id};

/// Merges by player, preferring GSI values, keeping every player from both sides.
pub fn merge(gsi_players: &[MatchPlayer], external_players: &[MatchPlayer]) -> Vec<MatchPlayer> {
    if external_players.is_empty() {
        return gsi_players.to_vec();
    }

    let find_gsi = |key: &str| {
        gsi_players
            .iter()
            .find(|player| !key.is_empty() && eq_ignore_case(&steam_key(&player.steam_id), key))
    };

    let mut merged: Vec<MatchPlayer> = external_players
        .iter()
        .map(|external| {
            let key = steam_key(&external.steam_id);
            match find_gsi(&key).filter(|_| !is_blank(&key)) {
                Some(gsi) => overlay_gsi_player(external, gsi),
                None => external.clone(),
            }
        })
        .collect();

    for gsi in gsi_players {
        let key = steam_key(&gsi.steam_id);
        if is_blank(&key) {
            continue;
        }
        if merged.iter().all(|player| !eq_ignore_case(&steam_key(&player.steam_id), &key)) {
            merged.push(gsi.clone());
        }
    }

    sort_by_team(&mut merged);
    merged
}

/// Playing GSI usually sends only the local player. Keep a previously fetched lobby unless
/// the match id changed or the new payload is already richer.
pub fn retain_if_richer(
    incoming: &[MatchPlayer],
    current: &[MatchPlayer],
    incoming_match_id: &str,
    current_match_id: &str,
) -> Vec<MatchPlayer> {
    if !is_same_match(current_match_id, incoming_match_id) {
        return incoming.to_vec();
    }
    let current_count = count_identified(current);
    if count_identified(incoming) >= current_count || current_count <= 1 {
        return incoming.to_vec();
    }
    merge(incoming, current)
}

/// False only when both ids are known and differ.
pub fn is_same_match(current_match_id: &str, incoming_match_id: &str) -> bool {
    !(match_id::is_usable(current_match_id)
        && match_id::is_usable(incoming_match_id)
        && !eq_ignore_case(current_match_id, incoming_match_id))
}

pub fn count_identified(players: &[MatchPlayer]) -> usize {
    players.iter().filter(|p| p.has_identity()).count()
}

pub fn count_heroes(players: &[MatchPlayer]) -> usize {
    players.iter().filter(|p| !p.hero_icon_key().is_empty()).count()
}

pub fn count_teamed(players: &[MatchPlayer]) -> usize {
    players.iter().filter(|p| team_sort_order(&p.team_name) < 2).count()
}

/// True until all ten players have an identity, a hero and a side.
pub fn needs_refresh(players: &[MatchPlayer]) -> bool {
    count_identified(players) < 10 || count_heroes(players) < 10 || count_teamed(players) < 10
}

/// Radiant, then Dire, then unknown; by slot, then name. Stable.
pub fn sort_by_team(players: &mut [MatchPlayer]) {
    players.sort_by(compare_by_team);
}

pub fn compare_by_team(a: &MatchPlayer, b: &MatchPlayer) -> Ordering {
    team_sort_order(&a.team_name)
        .cmp(&team_sort_order(&b.team_name))
        .then(a.team_slot.cmp(&b.team_slot))
        .then_with(|| cmp_ignore_case(&a.name, &b.name))
}

pub fn team_sort_order(team_name: &str) -> u8 {
    if contains_ignore_case(team_name, "radiant") {
        0
    } else if contains_ignore_case(team_name, "dire") {
        1
    } else {
        2
    }
}

fn overlay_gsi_player(external: &MatchPlayer, gsi: &MatchPlayer) -> MatchPlayer {
    let positive = |gsi: i32, external: i32| if gsi > 0 { gsi } else { external };
    MatchPlayer {
        steam_id: first_non_empty(&[&gsi.steam_id, &external.steam_id]),
        name: first_non_empty(&[&gsi.name, &external.name]),
        team_name: first_non_empty(&[&gsi.team_name, &external.team_name]),
        team_slot: positive(gsi.team_slot, external.team_slot),
        hero_name: first_non_empty(&[&gsi.hero_name, &external.hero_name]),
        hero_level: positive(gsi.hero_level, external.hero_level),
        kills: positive(gsi.kills, external.kills),
        deaths: positive(gsi.deaths, external.deaths),
        assists: positive(gsi.assists, external.assists),
        last_hits: positive(gsi.last_hits, external.last_hits),
        denies: positive(gsi.denies, external.denies),
        gpm: positive(gsi.gpm, external.gpm),
        xpm: positive(gsi.xpm, external.xpm),
        gold: positive(gsi.gold, external.gold),
        ..Default::default()
    }
}

/// 32-bit account id when parseable, otherwise the trimmed raw id.
fn steam_key(id: &str) -> String {
    steam_id::to_account_id(id)
        .map(|account| account.to_string())
        .unwrap_or_else(|| id.trim().to_string())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn player(id: impl ToString) -> MatchPlayer {
        MatchPlayer { steam_id: id.to_string(), ..Default::default() }
    }

    fn by_id<'a>(players: &'a [MatchPlayer], id: &str) -> &'a MatchPlayer {
        let found: Vec<_> = players.iter().filter(|p| p.steam_id == id).collect();
        assert_eq!(found.len(), 1, "exactly one player {id}");
        found[0]
    }

    #[test]
    fn needs_refresh_until_identities_and_heroes_are_complete() {
        for (count, heroes, expected) in [(1, 1, true), (10, 0, true), (10, 1, true), (10, 9, true), (10, 10, false)] {
            let players: Vec<_> = (0..count)
                .map(|i| MatchPlayer {
                    hero_name: if i < heroes { "npc_dota_hero_axe".into() } else { String::new() },
                    team_name: if i < 5 { "radiant".into() } else { "dire".into() },
                    ..player(i + 1)
                })
                .collect();
            assert_eq!(needs_refresh(&players), expected, "{count} players, {heroes} heroes");
        }
    }

    #[test]
    fn needs_refresh_true_when_full_roster_has_no_teams() {
        let players: Vec<_> =
            (1..=10).map(|i| MatchPlayer { hero_name: "npc_dota_hero_axe".into(), ..player(i) }).collect();
        assert!(needs_refresh(&players));
    }

    #[test]
    fn merge_fills_heroes_when_ten_players_were_already_identified() {
        let mut current: Vec<_> = (1..=10).map(|i| MatchPlayer { name: format!("Player {i}"), ..player(i) }).collect();
        current[0].hero_name = "npc_dota_hero_pudge".into();
        let refreshed: Vec<_> =
            (1..=10).map(|i| MatchPlayer { hero_name: "npc_dota_hero_axe".into(), ..player(i) }).collect();

        let merged = merge(&current, &refreshed);

        assert_eq!(merged.len(), 10);
        assert_eq!(by_id(&merged, "1").hero_name, "npc_dota_hero_pudge");
        assert!(merged.iter().filter(|p| p.steam_id != "1").all(|p| p.hero_icon_key() == "axe"));
        assert_eq!(by_id(&merged, "2").name, "Player 2");
    }

    #[test]
    fn merge_prefers_external_when_gsi_roster_is_sparse() {
        let gsi = [MatchPlayer {
            name: "Me".into(),
            team_name: "radiant".into(),
            hero_name: "npc_dota_hero_pudge".into(),
            ..player(1)
        }];
        let external: Vec<_> = (1..=10)
            .map(|i| MatchPlayer {
                name: format!("Player {i}"),
                team_name: if i <= 5 { "radiant".into() } else { "dire".into() },
                team_slot: if i <= 5 { i - 1 } else { i - 6 },
                hero_name: "npc_dota_hero_axe".into(),
                ..player(i)
            })
            .collect();

        let merged = merge(&gsi, &external);

        assert_eq!(merged.len(), 10);
        assert_eq!(by_id(&merged, "1").name, "Me");
        assert_eq!(by_id(&merged, "1").hero_name, "npc_dota_hero_pudge");
    }

    #[test]
    fn merge_overlays_gsi_player_when_steam_id_formats_differ() {
        let gsi = [MatchPlayer {
            name: "Me".into(),
            team_name: "radiant".into(),
            hero_name: "npc_dota_hero_pudge".into(),
            ..player("76561197960265729")
        }];
        let external = [
            MatchPlayer {
                name: "Account 1".into(),
                team_name: "radiant".into(),
                hero_name: "npc_dota_hero_axe".into(),
                ..player(1)
            },
            MatchPlayer { name: "Ally".into(), team_name: "radiant".into(), ..player(2) },
        ];

        let merged = merge(&gsi, &external);

        assert_eq!(merged.len(), 2);
        let me: Vec<_> = merged.iter().filter(|p| steam_id::to_account_id(&p.steam_id) == Some(1)).collect();
        assert_eq!(me.len(), 1);
        assert_eq!(me[0].name, "Me");
        assert_eq!(me[0].hero_name, "npc_dota_hero_pudge");
    }

    #[test]
    fn merge_keeps_richer_gsi_roster() {
        let gsi: Vec<_> = (1..=10)
            .map(|i| MatchPlayer {
                name: format!("Gsi {i}"),
                team_name: if i <= 5 { "radiant".into() } else { "dire".into() },
                ..player(i)
            })
            .collect();
        let external = [MatchPlayer { name: "External".into(), team_name: "radiant".into(), ..player(1) }];

        let merged = merge(&gsi, &external);

        assert_eq!(merged.len(), 10);
        assert!(merged.iter().all(|p| p.name.starts_with("Gsi")));
    }

    #[test]
    fn retain_if_richer_keeps_fetched_lobby_across_sparse_gsi_updates() {
        let incoming = [MatchPlayer {
            name: "Me".into(),
            team_name: "radiant".into(),
            hero_name: "npc_dota_hero_pudge".into(),
            ..player(1)
        }];
        let current: Vec<_> = (1..=10)
            .map(|i| MatchPlayer {
                name: format!("Player {i}"),
                team_name: if i <= 5 { "radiant".into() } else { "dire".into() },
                ..player(i)
            })
            .collect();

        let retained = retain_if_richer(&incoming, &current, "100", "100");

        assert_eq!(retained.len(), 10);
        assert_eq!(by_id(&retained, "1").name, "Me");
        assert_eq!(by_id(&retained, "1").hero_name, "npc_dota_hero_pudge");
    }

    #[test]
    fn retain_if_richer_drops_previous_lobby_when_match_id_changes() {
        let incoming = [MatchPlayer { name: "Me".into(), team_name: "radiant".into(), ..player(99) }];
        let current: Vec<_> = (1..=10)
            .map(|i| MatchPlayer { name: format!("Old {i}"), team_name: "radiant".into(), ..player(i) })
            .collect();

        let retained = retain_if_richer(&incoming, &current, "200", "100");

        assert_eq!(retained.len(), 1);
        assert_eq!(retained[0].steam_id, "99");
    }
}
