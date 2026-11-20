//! GSI payload → [`MatchSnapshot`] (C# `GsiNormalizer`).

use chrono::Utc;
use indexmap::IndexMap;

use super::payload::*;
use crate::match_id;
use crate::model::{format_internal_name, DraftSnapshot, MatchPlayer, MatchSnapshot, SlotSnapshot};
use crate::roster::compare_by_team;
use crate::text::contains_ignore_case;

pub fn normalize(payload: &GsiPayload) -> MatchSnapshot {
    let local_player = payload.player.as_ref().and_then(|p| p.local.as_ref());
    let local_hero = payload.hero.as_ref().and_then(|h| h.local.as_ref());
    let map = payload.map.as_ref();
    let text = |value: Option<&Option<String>>| value.and_then(|v| v.clone()).unwrap_or_default();
    let stat = |read: fn(&GsiPlayer) -> i32| local_player.map(read).unwrap_or_default();
    let team_name = text(local_player.map(|p| &p.team_name));

    MatchSnapshot {
        received_at_utc: Utc::now(),
        is_connected: true,
        game_state: text(map.map(|m| &m.game_state)),
        clock_time_seconds: map.map(|m| m.clock_time).unwrap_or_default(),
        game_time_seconds: map.map(|m| m.game_time).unwrap_or_default(),
        is_daytime: map.is_none_or(|m| m.daytime),
        match_id: match_id::normalize(&text(map.map(|m| &m.match_id))),
        server_steam_id: text(map.map(|m| &m.server_steam_id)).trim().to_string(),
        radiant_score: map.map(|m| m.radiant_score).unwrap_or_default(),
        dire_score: map.map(|m| m.dire_score).unwrap_or_default(),
        net_worth_lead: map
            .and_then(|m| m.radiant_gold_adv.as_ref())
            .and_then(|adv| adv.last().copied())
            .unwrap_or_default(),
        player_name: text(local_player.map(|p| &p.name)),
        steam_id: text(local_player.map(|p| &p.steam_id)),
        kills: stat(|p| p.kills),
        deaths: stat(|p| p.deaths),
        assists: stat(|p| p.assists),
        last_hits: stat(|p| p.last_hits),
        denies: stat(|p| p.denies),
        gpm: stat(|p| p.gpm),
        xpm: stat(|p| p.xpm),
        gold: stat(|p| p.gold),
        hero_name: format_internal_name(&text(local_hero.map(|h| &h.name))),
        hero_level: local_hero.map(|h| h.level).unwrap_or_default(),
        health: local_hero.map(|h| h.health).unwrap_or_default(),
        max_health: local_hero.map(|h| h.max_health).unwrap_or_default(),
        mana: local_hero.map(|h| h.mana).unwrap_or_default(),
        max_mana: local_hero.map(|h| h.max_mana).unwrap_or_default(),
        hero_alive: local_hero.is_none_or(|h| h.alive),
        items: normalize_slots(payload.items.as_ref(), |slot, item| SlotSnapshot {
            slot,
            name: format_internal_name(item.name.as_deref().unwrap_or_default()),
            level: 0,
            cooldown: item.cooldown,
            can_cast: item.can_cast,
        }),
        abilities: normalize_slots(payload.abilities.as_ref(), |slot, ability| SlotSnapshot {
            slot,
            name: format_internal_name(ability.name.as_deref().unwrap_or_default()),
            level: ability.level,
            cooldown: ability.cooldown,
            can_cast: ability.can_cast,
        }),
        draft: normalize_draft(payload.draft.as_ref(), &team_name),
        players: normalize_players(payload),
        team_name,
    }
}

/// Playing GSI often sends a 1-entry `allplayers`; the richest roster wins, earlier sources on ties.
pub(crate) fn normalize_players(payload: &GsiPayload) -> Vec<MatchPlayer> {
    let candidates = [all_players(payload), spectating_teams(payload), local_player(payload)];
    let mut best: Vec<MatchPlayer> = Vec::new();
    let mut best_score = (0, 0);
    for candidate in candidates {
        let score = (candidate.iter().filter(|p| p.has_identity()).count(), candidate.len());
        if !candidate.is_empty() && (best.is_empty() || score > best_score) {
            best_score = score;
            best = candidate;
        }
    }
    best
}

fn all_players(payload: &GsiPayload) -> Vec<MatchPlayer> {
    let Some(all) = payload.all_players.as_ref() else {
        return Vec::new();
    };
    let mut players: Vec<_> = all
        .iter()
        .map(|(key, player)| {
            let mut player = player.clone();
            apply_team_hints(&mut player, key);
            to_match_player(&player, player.hero.as_ref())
        })
        .filter(MatchPlayer::has_identity)
        .collect();
    players.sort_by(compare_by_team);
    players
}

fn spectating_teams(payload: &GsiPayload) -> Vec<MatchPlayer> {
    let Some(teams) = payload.player.as_ref().and_then(|p| p.teams.as_ref()) else {
        return Vec::new();
    };
    let heroes = payload.hero.as_ref().and_then(|h| h.teams.as_ref());
    let mut players: Vec<_> = teams
        .iter()
        .flat_map(|(team, slots)| slots.iter().map(move |(slot, player)| (team, slot, player)))
        .map(|(team, slot, player)| {
            let mut player = player.clone();
            apply_team_from_key(&mut player, team);
            apply_team_hints(&mut player, slot);
            let hero = find_spectated_hero(heroes, team, slot).or(player.hero.as_ref());
            to_match_player(&player, hero)
        })
        .filter(MatchPlayer::has_identity)
        .collect();
    players.sort_by(compare_by_team);
    players
}

fn local_player(payload: &GsiPayload) -> Vec<MatchPlayer> {
    let Some(local) = payload.player.as_ref().and_then(|p| p.local.as_ref()) else {
        return Vec::new();
    };
    let hero = payload.hero.as_ref().and_then(|h| h.local.as_ref()).or(local.hero.as_ref());
    let player = to_match_player(local, hero);
    if player.has_identity() {
        vec![player]
    } else {
        Vec::new()
    }
}

fn to_match_player(player: &GsiPlayer, hero: Option<&GsiHero>) -> MatchPlayer {
    let steam_id = player.steam_id.clone().unwrap_or_default();
    let name = match player.name.as_deref() {
        Some(name) if !name.trim().is_empty() => name.to_string(),
        _ if !steam_id.trim().is_empty() => steam_id.clone(),
        _ => "Unknown".into(),
    };
    MatchPlayer {
        steam_id,
        name,
        team_name: resolve_team_name(player),
        team_slot: player.team_slot,
        hero_name: hero.and_then(|h| h.name.clone()).unwrap_or_default(),
        hero_level: hero.map(|h| h.level).unwrap_or_default(),
        kills: player.kills,
        deaths: player.deaths,
        assists: player.assists,
        last_hits: player.last_hits,
        denies: player.denies,
        gpm: player.gpm,
        xpm: player.xpm,
        gold: player.gold,
        ..Default::default()
    }
}

fn blank(value: &Option<String>) -> bool {
    value.as_deref().is_none_or(|v| v.trim().is_empty())
}

fn set_team(player: &mut GsiPlayer, radiant: bool) {
    player.team_name = Some(if radiant { "radiant" } else { "dire" }.into());
    player.team = if radiant { 2 } else { 3 };
}

fn apply_team_from_key(player: &mut GsiPlayer, team_key: &str) {
    let side = if team_key.eq_ignore_ascii_case("team2") {
        Some(true)
    } else if team_key.eq_ignore_ascii_case("team3") {
        Some(false)
    } else {
        None
    };
    let Some(radiant) = side else { return };
    if blank(&player.team_name) {
        player.team_name = Some(if radiant { "radiant" } else { "dire" }.into());
    }
    if player.team == 0 {
        player.team = if radiant { 2 } else { 3 };
    }
}

fn apply_team_hints(player: &mut GsiPlayer, map_key: &str) {
    if !blank(&player.team_name) || matches!(player.team, 2 | 3) {
        return;
    }

    // Keys like "team2.player0" from flattened spectator allplayers.
    if contains_ignore_case(map_key, "team2") {
        return set_team(player, true);
    }
    if contains_ignore_case(map_key, "team3") {
        return set_team(player, false);
    }

    if let Some(index) = parse_player_index(map_key) {
        // Conventional GSI player0–player4 Radiant, player5–player9 Dire.
        set_team(player, index < 5);
        if player.team_slot == 0 {
            player.team_slot = index % 5;
        }
        return;
    }

    match player.player_slot {
        Some(slot @ 128..=132) => {
            set_team(player, false);
            player.team_slot = slot - 128;
        }
        Some(slot @ 0..=4) => {
            set_team(player, true);
            player.team_slot = slot;
        }
        Some(slot @ 5..=9) => {
            set_team(player, false);
            player.team_slot = slot - 5;
        }
        _ => {}
    }
}

/// The number after the last `player` in a key: `player7` → 7.
fn parse_player_index(key: &str) -> Option<i32> {
    let lower = key.to_ascii_lowercase();
    let start = lower.rfind("player")?;
    key[start + "player".len()..].trim().parse().ok()
}

fn find_spectated_hero<'a>(
    heroes: Option<&'a TeamMap<GsiHero>>,
    team_key: &str,
    player_key: &str,
) -> Option<&'a GsiHero> {
    let heroes = heroes?;
    heroes
        .iter()
        .find(|(team, _)| team.eq_ignore_ascii_case(team_key))
        .and_then(|(_, slots)| slots.get(player_key))
        .or_else(|| heroes.values().find_map(|slots| slots.get(player_key)))
}

fn resolve_team_name(player: &GsiPlayer) -> String {
    if !blank(&player.team_name) {
        return player.team_name.clone().unwrap_or_default();
    }
    match player.team {
        2 => "radiant".into(),
        3 => "dire".into(),
        _ => String::new(),
    }
}

fn normalize_slots<T>(
    slots: Option<&IndexMap<String, T>>,
    to_snapshot: impl Fn(String, &T) -> SlotSnapshot,
) -> Vec<SlotSnapshot> {
    let Some(slots) = slots else { return Vec::new() };
    let mut entries: Vec<_> = slots.iter().collect();
    entries.sort_by(|a, b| a.0.cmp(b.0));
    entries
        .into_iter()
        .map(|(slot, value)| to_snapshot(slot.clone(), value))
        .filter(|s| !s.name.trim().is_empty() && s.name != "empty")
        .collect()
}

fn normalize_draft(draft: Option<&GsiDraft>, team_name: &str) -> DraftSnapshot {
    let Some(draft) = draft else { return DraftSnapshot::default() };
    let hero_ids = |team: &Option<GsiDraftTeam>| {
        team.as_ref().map(|t| t.pick_ids.iter().copied().filter(|&id| id > 0).collect()).unwrap_or_default()
    };
    DraftSnapshot {
        is_active: true,
        active_team: draft.active_team,
        is_pick_phase: draft.pick,
        time_remaining: draft.active_team_time_remaining,
        player_is_radiant: contains_ignore_case(team_name, "radiant"),
        radiant_hero_ids: hero_ids(&draft.team2),
        dire_hero_ids: hero_ids(&draft.team3),
    }
}
