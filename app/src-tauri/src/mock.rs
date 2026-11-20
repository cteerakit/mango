//! Sample match for designing the UI without Dota (C# `MockMatchData`).

use chrono::{Duration, Utc};
use mango_core::assets;
use mango_core::history::{MatchBuildItem, PlayerMatchHistory, RecentMatchSummary};
use mango_core::model::{MatchPlayer, MatchSnapshot, SlotSnapshot};
use mango_core::rank_estimate::format_rank_tier;

use crate::opendota::{PlayerMatchRow, PlayerOverview};

pub fn snapshot() -> MatchSnapshot {
    let slot = |slot: &str, name: &str, level: i32| SlotSnapshot { slot: slot.into(), name: name.into(), level, ..Default::default() };
    MatchSnapshot {
        received_at_utc: Utc::now(),
        is_connected: true,
        game_state: "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS".into(),
        clock_time_seconds: 228,
        game_time_seconds: 32 * 60 + 48,
        is_daytime: true,
        match_id: "debug-preview".into(),
        radiant_score: 24,
        dire_score: 19,
        net_worth_lead: 1840,
        player_name: "Mango Tester".into(),
        steam_id: "76561198000000001".into(),
        kills: 8,
        deaths: 2,
        assists: 6,
        last_hits: 214,
        denies: 12,
        gpm: 612,
        xpm: 704,
        gold: 2840,
        team_name: "radiant".into(),
        hero_name: "npc_dota_hero_juggernaut".into(),
        hero_level: 18,
        health: 1680,
        max_health: 2100,
        mana: 420,
        max_mana: 600,
        hero_alive: true,
        items: vec![slot("slot0", "bfury", 0), slot("slot1", "power_treads", 0), slot("slot2", "manta", 0)],
        abilities: vec![
            slot("ability0", "blade_fury", 4),
            slot("ability1", "healing_ward", 3),
            slot("ability2", "blade_dance", 4),
            slot("ability3", "omni_slash", 3),
        ],
        players: players(),
        ..Default::default()
    }
}

pub fn build_suggestions() -> Vec<String> {
    ["Quelling Blade", "Power Treads", "Battle Fury", "Manta Style", "Butterfly", "Abyssal Blade", "Satanic"]
        .map(String::from)
        .to_vec()
}

#[allow(clippy::too_many_arguments)]
fn player(name: &str, steam_id: &str, team: &str, hero: &str, level: i32, k: i32, d: i32, a: i32, lh: i32, dn: i32, gpm: i32, xpm: i32, gold: i32) -> MatchPlayer {
    MatchPlayer {
        name: name.into(),
        steam_id: steam_id.into(),
        team_name: team.into(),
        hero_name: format!("npc_dota_hero_{hero}"),
        hero_level: level,
        kills: k,
        deaths: d,
        assists: a,
        last_hits: lh,
        denies: dn,
        gpm,
        xpm,
        gold,
        ..Default::default()
    }
}

pub fn players() -> Vec<MatchPlayer> {
    vec![
        player("Mango Tester", "76561198000000001", "radiant", "juggernaut", 18, 8, 2, 6, 214, 12, 612, 704, 2840),
        player("Radiant Mid", "", "radiant", "storm_spirit", 17, 6, 4, 9, 168, 8, 548, 692, 1960),
        player("Radiant Offlane", "", "radiant", "tidehunter", 16, 2, 5, 14, 92, 6, 412, 488, 1240),
        player("Radiant Support", "", "radiant", "crystal_maiden", 14, 1, 7, 18, 28, 2, 286, 392, 620),
        player("Radiant Hard Support", "", "radiant", "lich", 13, 0, 6, 16, 18, 1, 248, 356, 480),
        player("Dire Carry", "", "dire", "phantom_assassin", 17, 7, 3, 4, 198, 10, 584, 668, 2520),
        player("Dire Mid", "", "dire", "invoker", 16, 5, 4, 7, 142, 14, 512, 644, 1780),
        player("Dire Offlane", "", "dire", "axe", 15, 3, 6, 11, 84, 4, 396, 452, 980),
        player("Dire Support", "", "dire", "shadow_shaman", 13, 2, 8, 12, 22, 0, 268, 368, 540),
        player("Dire Hard Support", "", "dire", "warlock", 12, 1, 9, 13, 14, 1, 224, 332, 420),
    ]
}

struct Preview {
    tier: i32,
    matches: &'static str,
    encounter: &'static str,
    hero_match: &'static str,
    items: &'static [&'static str],
    private: bool,
    kda: &'static str,
    estimated: bool,
}

fn preview(name: &str) -> Option<Preview> {
    let p = |tier, matches, encounter, hero_match, items, kda| Preview { tier, matches, encounter, hero_match, items, private: false, kda, estimated: false };
    Some(match name {
        "Mango Tester" => p(75, "4,210 (52.4%)", "That's you.", "Latest on Juggernaut: Win · 31:12",
            &["bfury", "power_treads", "manta", "black_king_bar", "butterfly", "abyssal_blade"], "12/3/9"),
        "Radiant Mid" => p(63, "2,964 (50.1%)", "3/1", "Latest on Storm Spirit: Loss · 27:40",
            &["bloodstone", "kaya_and_sange", "blink", "travel_boots", "shivas_guard", "octarine_core"], "7/8/11"),
        "Radiant Offlane" => p(54, "1,508 (49.3%)", "0/0", "Latest on Tidehunter: Win · 35:02",
            &["blink", "pipe", "crimson_guard", "arcane_boots", "heart"], "4/5/23"),
        "Radiant Support" => p(41, "902 (47.8%)", "1/0", "Latest on Crystal Maiden: Win · 29:55",
            &["glimmer_cape", "force_staff", "tranquil_boots", "aether_lens"], "3/7/28"),
        "Radiant Hard Support" => p(35, "611 (51.0%)", "0/0", "Latest on Lich: Loss · 38:18",
            &["spirit_vessel", "glimmer_cape", "arcane_boots", "ultimate_scepter"], "2/10/17"),
        "Dire Carry" => p(71, "3,377 (54.9%)", "0/2", "Latest on Phantom Assassin: Win · 26:44",
            &["desolator", "phase_boots", "black_king_bar", "satanic", "abyssal_blade", "butterfly"], "18/2/7"),
        "Dire Mid" => p(62, "2,120 (48.6%)", "2/2", "Latest on Invoker: Win · 33:09",
            &["hand_of_midas", "witch_blade", "travel_boots", "ultimate_scepter", "sheepstick"], "9/4/16"),
        "Dire Offlane" => Preview { estimated: true, ..p(44, "1,244 (50.8%)", "0/0", "Latest on Axe: Loss · 30:21",
            &["vanguard", "blink", "blade_mail", "boots", "heart"], "5/9/12") },
        "Dire Support" => Preview { private: true, ..p(22, "—", "Encounter history unavailable.", "Hero match history unavailable.", &[], "—") },
        "Dire Hard Support" => p(53, "781 (46.2%)", "0/0", "Latest on Warlock: Loss · 41:07",
            &["glimmer_cape", "ultimate_scepter", "aether_lens", "arcane_boots"], "1/8/25"),
        _ => return None,
    })
}

/// Fills the OpenDota/Steam-derived fields so the preview renders offline.
pub fn apply_preview_details(player: &mut MatchPlayer) {
    let Some(d) = preview(&player.name) else { return };
    player.rank_icon_url = assets::rank_icon(Some(d.tier));
    player.rank_star_url = assets::rank_star(Some(d.tier));
    player.rank_label = if d.estimated {
        format!("Estimated: {} (from 12 recent solo ranked lobbies)", format_rank_tier(Some(d.tier), None))
    } else {
        format_rank_tier(Some(d.tier), None)
    };
    player.rank_is_estimated = d.estimated;
    player.reported_rank_tier = (!d.estimated).then_some(d.tier);
    player.matches_label = d.matches.into();
    player.is_profile_private = d.private;
    let position = match player.name.as_str() {
        "Radiant Mid" | "Dire Mid" => 2,
        "Radiant Offlane" | "Dire Offlane" => 3,
        "Radiant Support" | "Dire Support" => 4,
        "Radiant Hard Support" | "Dire Hard Support" => 5,
        _ => 1,
    };
    player.history = PlayerMatchHistory {
        encounter_label: d.encounter.into(),
        kda: d.kda.into(),
        position: (!d.private).then_some(position),
        result: if d.private { String::new() } else if d.hero_match.contains("Loss") { "Loss".into() } else { "Win".into() },
        played_at: (!d.private).then(|| Utc::now() - Duration::hours(5)),
        hero_match_label: d.hero_match.into(),
        hero_match_url: if d.private { String::new() } else { "https://www.opendota.com/matches/8000000000".into() },
        recent_matches_label: if d.private { "Recent matches unavailable." } else { "Last 5 completed matches, newest first" }.into(),
        recent_matches: if d.private {
            Vec::new()
        } else {
            players()
                .iter()
                .take(5)
                .enumerate()
                .map(|(i, hero)| RecentMatchSummary {
                    hero_name: hero.display_hero_name(),
                    hero_image_url: assets::hero_icon(&hero.hero_icon_key()),
                    result: if i % 3 == 1 { "Loss" } else { "Win" }.into(),
                })
                .collect()
        },
        items: d
            .items
            .iter()
            .enumerate()
            .map(|(i, item)| MatchBuildItem {
                name: item.replace('_', " "),
                image_url: assets::item_icon(item),
                slot: "Item".into(),
                purchase_time_seconds: Some(175 + i as i32 * 227),
            })
            .collect(),
        ..Default::default()
    };
}

pub fn player_overview(steam_id: &str) -> PlayerOverview {
    let all = players();
    let mut player = all.iter().find(|p| p.steam_id == steam_id).unwrap_or(&all[0]).clone();
    apply_preview_details(&mut player);
    PlayerOverview {
        steam_id: steam_id.into(),
        display_name: player.name.clone(),
        rank_tier: player.reported_rank_tier,
        display_rank_tier: player.reported_rank_tier,
        rank_label: player.rank_label.clone(),
        is_private: player.is_profile_private,
        wins: 2206,
        losses: 2004,
        win_rate_percent: Some(52.4),
        recent_matches: vec![PlayerMatchRow {
            match_id: "8000000000".into(),
            hero_name: player.display_hero_name(),
            result: "Win".into(),
            kda: "12/3/9".into(),
            duration: "31:12".into(),
            played_at: "Today".into(),
        }],
        ..Default::default()
    }
}
