//! Ported from `GsiNormalizerTests.cs`, plus checks for the lenient parsing.

use super::*;

fn parse(json: &str) -> GsiPayload {
    GsiPayload::parse(json).expect("valid GSI json")
}

#[test]
fn formats_clock_and_kda() {
    let payload = parse(
        r#"{
          "map": { "clock_time": 754, "game_state": "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS", "matchid": "8345123456" },
          "player": { "name": "Invoker", "steamid": "76561198000000000", "kills": 3, "deaths": 1, "assists": 5,
                      "gpm": 420, "xpm": 510, "gold": 1250 },
          "hero": { "name": "npc_dota_hero_invoker", "level": 12, "health": 800, "max_health": 1000,
                    "mana": 300, "max_mana": 400 }
        }"#,
    );

    let snapshot = normalize(&payload);

    assert_eq!(snapshot.formatted_clock(), "12:34");
    assert_eq!(snapshot.kda(), "3/1/5");
    assert_eq!(snapshot.player_name, "Invoker");
    assert_eq!(snapshot.steam_id, "76561198000000000");
    assert_eq!(snapshot.match_id, "8345123456");
    assert_eq!(snapshot.hero_name, "invoker");
    assert!((snapshot.health_percent() - 0.8).abs() < 1e-3);
    assert_eq!(snapshot.players.len(), 1);
    assert_eq!(snapshot.players[0].display_hero_name(), "invoker");
}

#[test]
fn ignores_zero_match_id() {
    let snapshot = normalize(&parse(r#"{ "map": { "matchid": "0" } }"#));
    assert_eq!(snapshot.match_id, "");
}

#[test]
fn maps_all_players_roster() {
    let payload = parse(
        r#"{ "allplayers": {
          "player1": { "name": "Dire Mid", "team": 3, "team_slot": 1, "kills": 5, "deaths": 2, "assists": 4,
                       "last_hits": 120, "denies": 8, "gpm": 500, "xpm": 600, "gold": 1800,
                       "hero": { "name": "npc_dota_hero_invoker", "level": 16 } },
          "player0": { "name": "Radiant Carry", "team_name": "radiant", "team_slot": 0, "kills": 8, "deaths": 1,
                       "assists": 3, "last_hits": 200, "denies": 12, "gpm": 620, "xpm": 710, "gold": 2400,
                       "hero": { "name": "npc_dota_hero_juggernaut", "level": 18 } }
        } }"#,
    );

    let snapshot = normalize(&payload);

    assert_eq!(snapshot.players.len(), 2);
    assert_eq!(snapshot.players[0].name, "Radiant Carry");
    assert_eq!(snapshot.players[0].team_name, "radiant");
    assert_eq!(snapshot.players[0].display_hero_name(), "juggernaut");
    assert_eq!(snapshot.players[1].name, "Dire Mid");
    assert_eq!(snapshot.players[1].team_name, "dire");
    assert_eq!(snapshot.players[1].kda(), "5/2/4");
}

#[test]
fn maps_spectated_team_players_from_json() {
    let payload = parse(
        r#"{
          "map": { "matchid": "9001", "game_state": "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS", "clock_time": 120 },
          "player": {
            "team2": { "player0": { "steamid": "76561198000000001", "name": "Radiant Carry", "team_slot": 0,
              "kills": 8, "deaths": 1, "assists": 3, "last_hits": 200, "denies": 12, "gpm": 620, "xpm": 710, "gold": 2400 } },
            "team3": { "player5": { "steamid": "76561198000000002", "name": "Dire Mid", "team_slot": 1,
              "kills": 5, "deaths": 2, "assists": 4, "last_hits": 120, "denies": 8, "gpm": 500, "xpm": 600, "gold": 1800 } }
          },
          "hero": {
            "team2": { "player0": { "name": "npc_dota_hero_juggernaut", "level": 18 } },
            "team3": { "player5": { "name": "npc_dota_hero_invoker", "level": 16 } }
          },
          "items": { "team2": { "player0": { "slot0": { "name": "item_blink" } } } },
          "abilities": { "team2": { "player0": { "ability0": { "name": "juggernaut_blade_fury", "level": 4 } } } }
        }"#,
    );
    assert!(payload.player.as_ref().unwrap().is_spectating());
    assert!(payload.items.is_none());
    assert!(payload.abilities.is_none());

    let snapshot = normalize(&payload);

    assert_eq!(snapshot.match_id, "9001");
    assert_eq!(snapshot.players.len(), 2);
    let (radiant, dire) = (&snapshot.players[0], &snapshot.players[1]);
    assert_eq!(radiant.name, "Radiant Carry");
    assert_eq!(radiant.team_name, "radiant");
    assert_eq!(radiant.steam_id, "76561198000000001");
    assert_eq!(radiant.display_hero_name(), "juggernaut");
    assert_eq!(radiant.hero_level, 18);
    assert_eq!(dire.name, "Dire Mid");
    assert_eq!(dire.team_name, "dire");
    assert_eq!(dire.display_hero_name(), "invoker");
    assert_eq!(dire.kda(), "5/2/4");
    assert!(snapshot.items.is_empty());
    assert!(snapshot.abilities.is_empty());
}

#[test]
fn maps_local_player_during_hero_selection_from_json() {
    // Playing-mode GSI includes team_name/team/team_slot on the flat player object.
    // Those must not be mistaken for spectator team2/team3 maps (common during pick).
    let payload = parse(
        r#"{
          "map": { "matchid": "8345123456", "server_steam_id": "90123456789012345",
                   "game_state": "DOTA_GAMERULES_STATE_HERO_SELECTION", "clock_time": -45 },
          "player": { "steamid": "76561198000000001", "name": "Mango Tester", "activity": "playing",
                      "team_name": "radiant", "team": 2, "team_slot": 0, "kills": 0, "deaths": 0, "assists": 0 },
          "hero": { "id": 0, "name": "" }
        }"#,
    );
    let node = payload.player.as_ref().unwrap();
    assert!(!node.is_spectating());
    assert_eq!(node.local.as_ref().unwrap().team_name.as_deref(), Some("radiant"));

    let snapshot = normalize(&payload);

    assert_eq!(snapshot.game_state, "DOTA_GAMERULES_STATE_HERO_SELECTION");
    assert_eq!(snapshot.match_id, "8345123456");
    assert_eq!(snapshot.server_steam_id, "90123456789012345");
    assert_eq!(snapshot.formatted_clock(), "-00:45");
    assert_eq!(snapshot.players.len(), 1);
    assert_eq!(snapshot.players[0].name, "Mango Tester");
    assert_eq!(snapshot.players[0].steam_id, "76561198000000001");
    assert_eq!(snapshot.players[0].team_name, "radiant");
}

#[test]
fn infers_teams_from_all_players_keys_when_team_name_missing() {
    let payload = parse(
        r#"{
          "map": { "matchid": "9002", "game_state": "DOTA_GAMERULES_STATE_HERO_SELECTION" },
          "player": { "steamid": "76561198000000001", "name": "Me", "team_name": "radiant", "team": 2 },
          "allplayers": {
            "player0": { "steamid": "76561198000000001", "name": "Me" },
            "player1": { "steamid": "76561198000000002", "name": "Ally" },
            "player5": { "steamid": "76561198000000006", "name": "Enemy" }
          }
        }"#,
    );
    assert_eq!(payload.all_players.as_ref().map(|p| p.len()), Some(3));

    let snapshot = normalize(&payload);

    assert_eq!(snapshot.players.len(), 3);
    let has = |name: &str, team: &str| snapshot.players.iter().any(|p| p.name == name && p.team_name == team);
    assert!(has("Me", "radiant"));
    assert!(has("Ally", "radiant"));
    assert!(has("Enemy", "dire"));
}

#[test]
fn prefers_spectating_teams_over_sparse_all_players() {
    let payload = parse(
        r#"{
          "map": { "matchid": "9003", "game_state": "DOTA_GAMERULES_STATE_HERO_SELECTION" },
          "allplayers": { "player0": { "steamid": "76561198000000001", "name": "OnlyMe", "team_name": "radiant" } },
          "player": {
            "team2": {
              "player0": { "steamid": "76561198000000001", "name": "Radiant A", "team_slot": 0 },
              "player1": { "steamid": "76561198000000002", "name": "Radiant B", "team_slot": 1 }
            },
            "team3": { "player5": { "steamid": "76561198000000006", "name": "Dire A", "team_slot": 0 } }
          }
        }"#,
    );

    let snapshot = normalize(&payload);

    assert_eq!(snapshot.players.len(), 3);
    assert!(snapshot.players.iter().any(|p| p.name == "Radiant B"));
    assert!(snapshot.players.iter().any(|p| p.name == "Dire A"));
}

#[test]
fn local_items_and_abilities_sorted_without_empty_slots() {
    let payload = parse(
        r#"{
          "items": { "slot1": { "name": "item_blink", "cooldown": 3.5, "can_cast": true },
                     "slot0": { "name": "empty" }, "neutral0": { "name": "item_trusty_shovel" } },
          "abilities": { "ability0": { "name": "pudge_meat_hook", "level": 4 } }
        }"#,
    );

    let snapshot = normalize(&payload);

    let names: Vec<_> = snapshot.items.iter().map(|s| (s.slot.as_str(), s.name.as_str())).collect();
    assert_eq!(names, [("neutral0", "trusty_shovel"), ("slot1", "blink")]);
    assert_eq!(snapshot.items[1].cooldown, 3.5);
    assert_eq!(snapshot.abilities[0].level, 4);
}

#[test]
fn draft_lists_picked_heroes_by_side() {
    let payload = parse(
        r#"{
          "player": { "team_name": "dire", "name": "Me" },
          "draft": { "activeteam": 2, "pick": true, "activeteam_time_remaining": 25,
                     "team2": { "pick0_id": 14, "pick1_id": 0 }, "team3": { "pick0_id": 1, "pick1_id": 74 } }
        }"#,
    );

    let draft = normalize(&payload).draft;

    assert!(draft.is_active && draft.is_pick_phase);
    assert_eq!(draft.time_remaining, 25);
    assert_eq!(draft.ally_hero_ids(), [1, 74]);
    assert_eq!(draft.enemy_hero_ids(), [14]);
}

#[test]
fn player_slot_assigns_team_when_keys_carry_no_hint() {
    let payload = parse(
        r#"{ "allplayers": {
          "a": { "name": "Dire Two", "player_slot": 130 },
          "b": { "name": "Radiant One", "player_slot": 1 }
        } }"#,
    );

    let players = normalize(&payload).players;

    assert_eq!(players[0].name, "Radiant One");
    assert_eq!((players[0].team_name.as_str(), players[0].team_slot), ("radiant", 1));
    assert_eq!((players[1].team_name.as_str(), players[1].team_slot), ("dire", 2));
}

#[test]
fn lenient_about_number_shapes_and_big_ids() {
    let payload = parse(
        r#"{ "map": { "matchid": 8345123456789, "server_steam_id": 90153324321222656, "clock_time": "61",
                      "radiant_gold_adv": [100, -250] },
             "player": { "name": "Me", "team": "dire", "gpm": 512.0 } }"#,
    );

    let snapshot = normalize(&payload);

    assert_eq!(snapshot.match_id, "8345123456789");
    assert_eq!(snapshot.server_steam_id, "90153324321222656");
    assert_eq!(snapshot.clock_time_seconds, 61);
    assert_eq!(snapshot.net_worth_lead, -250);
    assert_eq!(snapshot.gpm, 512);
    assert_eq!(snapshot.players[0].team_name, "dire");
}

#[test]
fn empty_or_menu_payloads_produce_an_empty_snapshot() {
    let snapshot = normalize(&parse(r#"{ "provider": { "name": "Dota 2", "appid": 570 } }"#));
    assert!(snapshot.is_connected);
    assert!(snapshot.players.is_empty());
    assert!(snapshot.is_daytime, "no map → daytime, like the C# default");
    assert!(snapshot.hero_alive);
    assert!(GsiPayload::parse("{ not json").is_err());
}
