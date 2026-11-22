//! Ported from `OpenDotaServiceTests.cs`, `MatchHistoryTests.cs` and `MatchHistoryLoaderTests.cs`.

use std::sync::Arc;

use super::*;
use crate::http::fake::{path, query, FakeHttp};

const PUDGE: &str = r#"[{"id":14,"name":"npc_dota_hero_pudge","localized_name":"Pudge"}]"#;

fn service(http: Arc<FakeHttp>) -> (OpenDota, tempfile::TempDir) {
    let dir = tempfile::tempdir().unwrap();
    (OpenDota::for_tests(http, dir.path().to_path_buf()), dir)
}

fn hero_filter(request: &Request) -> String {
    let q = query(request);
    assert!(q.contains("hero_id=14") && q.contains("significant=0") && q.contains("limit=2"), "{q}");
    r#"[{"match_id":100,"hero_id":14,"start_time":1700000100},
        {"match_id":90,"hero_id":14,"start_time":1700000000,"player_slot":128,
         "radiant_win":true,"duration":1800,"kills":3,"deaths":4,"assists":5}]"#
        .into()
}

const SIMPLE_ITEMS: &str = r#"{"blink":{"id":1,"dname":"Blink Dagger"},"boots":{"id":2,"dname":"Boots of Speed"},
    "trusty_shovel":{"id":3,"dname":"Trusty Shovel"}}"#;

// ---- Player overview ---------------------------------------------------------------------

fn player_api(profile: Option<String>, ranked: Option<&'static str>, live: Option<&'static str>) -> Arc<FakeHttp> {
    FakeHttp::ok_or_404(move |r| {
        let p = path(r);
        Some(match p.as_str() {
            "/api/heroes" => "[]".into(),
            "/api/live" => live.unwrap_or("[]").into(),
            "/api/players/118993939" => profile.clone().unwrap_or(r#"{"profile":{"personaname":"Turbo player"}}"#.into()),
            "/api/players/118993939/recentMatches" => "[]".into(),
            "/api/players/118993939/matches" => {
                let q = query(r);
                assert!(q.contains("lobby_type=7") && q.contains("sort=start_time") && q.contains("limit=10&"));
                ranked.unwrap_or("[]").into()
            }
            "/api/matches/10" | "/api/matches/20" => r#"{"players":[{"player_slot":0},{"player_slot":1,"rank_tier":44},
                {"player_slot":2,"rank_tier":44},{"player_slot":128},{"player_slot":129,"rank_tier":44},
                {"player_slot":130,"rank_tier":44},{"player_slot":131,"rank_tier":44},{"player_slot":132,"rank_tier":44}]}"#
                .into(),
            "/api/players/118993939/wl" => {
                if query(r).contains("significant=0") { r#"{"win":1069,"lose":1118}"# } else { r#"{"win":1,"lose":4}"# }.into()
            }
            other => panic!("unexpected endpoint {other}"),
        })
    })
}

#[tokio::test]
async fn overview_includes_turbo_even_with_old_filtered_cache() {
    let (od, dir) = service(player_api(None, None, None));
    std::fs::write(dir.path().join("player_118993939_wl.json"), r#"{"win":1,"lose":4}"#).unwrap();

    let overview = od.player_overview("118993939").await.unwrap();

    assert_eq!(overview.wins + overview.losses, 2187);
    assert_eq!((overview.wins, overview.losses), (1069, 1118));
    assert_eq!(overview.win_rate_percent, Some(48.9));
    assert!(!overview.possible_smurf);
}

#[tokio::test]
async fn overview_flags_possible_smurfs_with_complete_public_stats() {
    for (private, record, expected) in [
        (false, r#"{"win":33,"lose":17}"#, true),
        (false, r#"{"win":49,"lose":0}"#, false),
        (false, r#"{"win":325,"lose":175}"#, true),
        (false, r#"{"win":326,"lose":175}"#, false),
        (false, r#"{"win":323,"lose":174}"#, false),
        (false, r#"{"win":100}"#, false),
        (true, r#"{"win":100,"lose":0}"#, false),
    ] {
        let profile = format!(r#"{{"profile":{{"fh_unavailable":{private}}}}}"#);
        let (od, dir) = service(player_api(Some(profile), None, None));
        std::fs::write(dir.path().join("player_118993939_wl_all_modes.json"), record).unwrap();
        let overview = od.player_overview("118993939").await.unwrap();
        assert_eq!(overview.possible_smurf, expected, "{private}, {record}");
    }
}

#[tokio::test]
async fn overview_loads_avatar_from_opendota() {
    for (full, medium, small, expected) in [
        (Some("full.png"), Some("medium.png"), Some("small.png"), "full.png"),
        (None, Some("medium.png"), Some("small.png"), "medium.png"),
        (Some(""), None, Some("small.png"), "small.png"),
        (None, None, None, ""),
    ] {
        let profile = serde_json::json!({"profile": {"personaname": "Player", "avatarfull": full, "avatarmedium": medium, "avatar": small}});
        let (od, _dir) = service(player_api(Some(profile.to_string()), None, None));
        let overview = od.player_overview("118993939").await.unwrap();
        assert_eq!(overview.display_name, "Player");
        assert_eq!(overview.avatar_url, expected);
    }
}

#[tokio::test]
async fn private_reported_rank_is_unverified() {
    for is_private in [true, false] {
        let profile = format!(r#"{{"rank_tier":63,"leaderboard_rank":123,"profile":{{"fh_unavailable":{is_private}}}}}"#);
        let (od, _dir) = service(player_api(Some(profile), None, None));
        let overview = od.player_overview("118993939").await.unwrap();
        assert_eq!(overview.is_private, is_private);
        assert_eq!(overview.display_rank_tier, Some(63));
        assert_eq!(overview.rank_is_estimated, is_private);
        if is_private {
            assert!(overview.rank_label.contains("private profile"));
            assert!(!overview.rank_label.contains("#123"));
        }
    }
}

#[tokio::test]
async fn estimates_from_recent_ranked_lobbies_and_record() {
    for old_tier in [None, Some(63)] {
        let ranked = r#"[{"match_id":10,"start_time":100,"lobby_type":7,"average_rank":44,"player_slot":0},
             {"match_id":20,"start_time":200,"lobby_type":7,"average_rank":44,"player_slot":128}]"#;
        let profile = serde_json::json!({"rank_tier": old_tier, "profile": {"fh_unavailable": true}});
        let (od, dir) = service(player_api(Some(profile.to_string()), Some(ranked), None));
        let ranked_cache = dir.path().join("player_118993939_ranked_recent.json");
        std::fs::write(
            &ranked_cache,
            r#"[{"match_id":10,"start_time":100,"lobby_type":7,"average_rank":11}]"#,
        )
        .unwrap();
        std::fs::OpenOptions::new()
            .write(true)
            .open(ranked_cache)
            .unwrap()
            .set_modified(SystemTime::now() - hours(1))
            .unwrap();

        let overview = od.player_overview("118993939").await.unwrap();

        assert_eq!(overview.display_rank_tier, Some(44));
        assert!(overview.rank_is_estimated);
        assert!(overview.rank_label.contains("teammates' ranks in 2 recent ranked matches"), "{}", overview.rank_label);
        assert!(overview.recent_matches.is_empty());
    }
}

#[tokio::test]
async fn match_roster_rejects_same_account_in_different_known_match() {
    let (od, _dir) = service(player_api(None, None, Some(r#"[{"match_id":"111","players":[{"account_id":118993939,"hero_id":14}]}]"#)));
    assert!(od.match_roster("222", Some(118_993_939)).await.is_none());
}

#[tokio::test]
async fn match_roster_maps_live_teams_and_heroes() {
    let live = r#"[{"match_id":222,"players":[{"account_id":5,"hero_id":14,"team":0,"team_slot":1},
        {"account_id":0,"hero_id":1},{"account_id":6,"hero_id":2,"team":1,"team_slot":3}]}]"#;
    let http = FakeHttp::ok_or_404(move |r| Some(if path(r) == "/api/heroes" { PUDGE.into() } else { live.into() }));
    let (od, _dir) = service(http);
    let players = od.match_roster("222", None).await.unwrap();
    assert_eq!(players.len(), 2);
    assert_eq!((players[0].team_name.as_str(), players[0].team_slot, players[0].hero_name.as_str()), ("radiant", 0, "npc_dota_hero_pudge"));
    assert_eq!((players[1].team_name.as_str(), players[1].team_slot), ("dire", 2));
}

// ---- Recent matches and encounters -------------------------------------------------------

#[tokio::test]
async fn recent_matches_show_five_newest_heroes_excluding_displayed_match() {
    let http = FakeHttp::ok_or_404(|r| {
        Some(match path(r).as_str() {
            "/api/heroes" => PUDGE.into(),
            "/api/players/456/recentMatches" => r#"[
                {"match_id":1,"hero_id":14,"start_time":1,"player_slot":0,"radiant_win":true},
                {"match_id":7,"hero_id":14,"start_time":7,"player_slot":0,"radiant_win":true},
                {"match_id":6,"hero_id":14,"start_time":6,"player_slot":128,"radiant_win":false},
                {"match_id":90,"hero_id":14,"start_time":5,"player_slot":128,"radiant_win":true},
                {"match_id":4,"hero_id":14,"start_time":4,"player_slot":0,"radiant_win":false},
                {"match_id":3,"hero_id":14,"start_time":3,"player_slot":0,"radiant_win":true},
                {"match_id":2,"hero_id":14,"start_time":2,"player_slot":128,"radiant_win":false}]"#
                .into(),
            other => panic!("unexpected {other}"),
        })
    });
    let (od, _dir) = service(http);
    let replay = FakeReplay { positions: None, fail: false, purchases: None };
    let (matches, _) = od.recent_match_summaries("76561197960266184", "7", Some(&replay)).await;
    let results: Vec<_> = matches.iter().map(|m| m.result.as_str()).collect();
    assert_eq!(results, ["Win", "Loss", "Loss", "Win", "Win"]);
    let outcomes: Vec<_> = matches.iter().map(|m| m.analysis_outcome).collect();
    assert_eq!(outcomes, [None, Some(MatchAnalysisOutcome::Stomp), None, None, None]);
    assert!(matches.iter().all(|m| m.hero_name == "Pudge" && m.hero_image_url.ends_with("/heroes/pudge.png")));
}

#[tokio::test]
async fn recent_matches_distinguish_empty_and_unavailable_history() {
    for (json, expected) in [(Some("[]"), "No recent matches recorded by OpenDota."), (None, "Recent matches unavailable.")] {
        let http = FakeHttp::ok_or_404(move |r| if path(r) == "/api/heroes" { Some("[]".into()) } else { json.map(String::from) });
        let (od, _dir) = service(http);
        let (matches, label) = od.recent_match_summaries("456", "100", None).await;
        assert!(matches.is_empty());
        assert_eq!(label, expected);
    }
}

#[tokio::test]
async fn encounters_normalize_steam_ids_include_turbo_and_exclude_displayed_match() {
    let http = FakeHttp::ok_or_404(|r| {
        if path(r) == "/api/heroes" {
            return Some(r#"[{"id":2,"name":"npc_dota_hero_axe","localized_name":"Axe"}]"#.into());
        }
        assert_eq!(path(r), "/api/players/456/matches");
        assert!(query(r).contains("significant=0"));
        Some(if query(r).contains("with_account_id=123") {
            r#"[{"match_id":90,"start_time":90},{"match_id":100,"start_time":100},{"match_id":50,"start_time":50},{"match_id":70,"start_time":70}]"#
        } else {
            r#"[{"match_id":95,"start_time":95,"hero_id":2,"player_slot":128,"radiant_win":false},{"match_id":60,"start_time":60},{"match_id":80,"start_time":80}]"#
        }.into())
    });
    let (od, _dir) = service(http);
    let (recent, label) = od.encounters("76561197960265851", "456", "100").await;
    assert_eq!(label, "3 with · 3 against");
    assert_eq!(
        recent.iter().map(|m| (m.match_id.as_str(), m.side)).collect::<Vec<_>>(),
        [
            ("95", EncounterSide::Against),
            ("90", EncounterSide::With),
            ("80", EncounterSide::Against),
            ("70", EncounterSide::With),
            ("60", EncounterSide::Against),
        ]
    );
    assert_eq!(recent[0].played_at.unwrap().timestamp(), 95);
    assert_eq!(recent[0].summary.hero_name, "Axe");
    assert_eq!(recent[0].summary.hero_image_url, assets::hero_icon("axe"));
    assert_eq!(recent[0].summary.result, "Loss");
}

#[tokio::test]
async fn empty_recorded_history_is_different_from_unavailable_history() {
    let (od, _dir) = service(FakeHttp::ok_or_404(|_| Some("[]".into())));
    assert_eq!(od.encounters("123", "456", "100").await, (vec![], "No shared matches recorded.".into()));
    let (failed, _dir) = service(FakeHttp::ok_or_404(|_| None));
    assert_eq!(failed.encounters("123", "789", "100").await, (vec![], "Encounter history unavailable.".into()));
    assert_eq!(failed.encounters("", "1", "100").await, (vec![], "Your account is needed for encounter history.".into()));
}

#[tokio::test]
async fn encounters_keep_available_matches_when_one_lookup_fails() {
    let (od, _dir) = service(FakeHttp::ok_or_404(|r| {
        query(r).contains("against_account_id=123").then(|| r#"[{"match_id":90}]"#.into())
    }));
    let (recent, label) = od.encounters("123", "456", "100").await;
    assert_eq!(label, "Some encounter history is unavailable.");
    assert_eq!(recent, [RecentEncounter {
        match_id: "90".into(),
        side: EncounterSide::Against,
        played_at: None,
        summary: RecentMatchSummary { hero_name: "Unknown hero".into(), ..Default::default() },
    }]);
}

// ---- Latest hero build -------------------------------------------------------------------

fn build_api(detail: String, items: &'static str) -> Arc<FakeHttp> {
    FakeHttp::ok_or_404(move |r| {
        Some(match path(r).as_str() {
            "/api/heroes" => PUDGE.into(),
            "/api/players/456/matches" => hero_filter(r),
            "/api/matches/90" => detail.clone(),
            "/api/constants/items" => items.into(),
            other => panic!("unexpected {other}"),
        })
    })
}

#[tokio::test]
async fn latest_build_uses_key_inventory_and_backpack_items_for_correct_player() {
    let detail = r#"{"players":[{"account_id":777,"hero_id":14,"item_0":999},
        {"account_id":456,"hero_id":14,"item_0":1,"item_1":1,"item_2":999,"item_3":0,"backpack_0":1,"item_neutral":3}]}"#;
    let (od, _dir) = service(build_api(detail.into(), SIMPLE_ITEMS));

    let build = od.latest_hero_build("456", "npc_dota_hero_pudge", "100", None).await;

    assert_eq!(build.hero_match_url, "https://www.opendota.com/matches/90");
    assert_eq!(build.kda, "3/4/5");
    assert_eq!(build.result_kda(), "Loss 3/4/5");
    assert_eq!(build.position, None);
    assert_eq!(build.position_label(), "Position unavailable");
    assert_eq!(build.played_at, Utc.timestamp_opt(1_700_000_000, 0).single());
    assert!(build.hero_match_label.contains("Latest Pudge · Loss · 3/4/5"), "{}", build.hero_match_label);
    let names: Vec<_> = build.items.iter().map(|i| i.name.as_str()).collect();
    assert_eq!(names, ["Blink Dagger"; 3]);
    assert!(build.items[0].image_url.ends_with("/blink.png"));
    assert_eq!(build.items[2].slot, "Backpack");
}

#[tokio::test]
async fn build_uses_purchase_timeline_when_available_and_final_inventory_otherwise() {
    for log in ["null", "[]", r#"[{"key":"blink","time":-30},{"key":"blink","time":1200},{"key":"blink","time":600}]"#] {
        let detail = format!(
            r#"{{"players":[{{"account_id":456,"hero_id":14,"item_0":1,"item_1":999,"item_2":1,"backpack_0":2,"item_neutral":3,"purchase_log":{log}}}]}}"#
        );
        let (od, _dir) = service(build_api(detail, SIMPLE_ITEMS));
        let build = od.latest_hero_build("456", "pudge", "100", None).await;
        if log.len() > 2 && log.starts_with('[') {
            assert_eq!(build.items.len(), 3);
            let times: Vec<_> = build.items.iter().map(|i| i.purchase_time_seconds).collect();
            assert_eq!(times, [Some(-30), Some(600), Some(1200)]);
            assert_eq!(build.items[0].slot, "Purchase");
        } else {
            assert_eq!(build.items.len(), 2, "{log}");
            assert!(build.items.iter().all(|i| i.purchase_time_seconds.is_none()));
        }
    }
}

const BUILD_ITEMS: &str = r#"{"tango":{"id":1,"dname":"Tango","cost":90},
    "boots":{"id":2,"dname":"Boots of Speed","cost":500},
    "mithril_hammer":{"id":3,"dname":"Mithril Hammer","cost":1600},
    "recipe_black_king_bar":{"id":4,"dname":"BKB Recipe","cost":1450},
    "black_king_bar":{"id":5,"dname":"Black King Bar","cost":4050,"components":["mithril_hammer","ogre_axe"]},
    "magic_wand":{"id":6,"dname":"Magic Wand","cost":450,"components":["magic_stick","branches"]},
    "force_staff":{"id":7,"dname":"Force Staff","cost":2200,"components":["staff_of_wizardry","fluffy_hat"]},
    "trusty_shovel":{"id":8,"dname":"Trusty Shovel","cost":0}}"#;

#[tokio::test]
async fn build_shows_only_key_items_for_purchases_and_final_inventory() {
    for has_log in [true, false] {
        let log = if has_log {
            r#","purchase_log":[{"key":"tango","time":0},{"key":"boots","time":175},{"key":"mithril_hammer","time":300},
              {"key":"recipe_black_king_bar","time":400},{"key":"black_king_bar","time":500},{"key":"magic_wand","time":600},
              {"key":"force_staff","time":700},{"key":"trusty_shovel","time":800}]"#
        } else {
            ""
        };
        let detail = format!(
            r#"{{"players":[{{"account_id":456,"hero_id":14,"item_0":1,"item_1":2,"item_2":3,"item_3":4,"item_4":5,"item_5":6,"backpack_0":7,"item_neutral":8{log}}}]}}"#
        );
        let (od, _dir) = service(build_api(detail, BUILD_ITEMS));
        let build = od.latest_hero_build("456", "pudge", "100", None).await;
        let names: Vec<_> = build.items.iter().map(|i| i.name.as_str()).collect();
        assert_eq!(names, ["Black King Bar", "Force Staff"]);
        if has_log {
            assert_eq!(build.items.iter().map(|i| i.purchase_time_seconds).collect::<Vec<_>>(), [Some(500), Some(700)]);
        }
    }
}

#[tokio::test]
async fn purchase_timeline_overflows_after_eleven_items() {
    let log: Vec<String> = (0..20).rev().map(|i| format!(r#"{{"key":"blink","time":{}}}"#, 175 + i * 60)).collect();
    let detail = format!(r#"{{"players":[{{"account_id":456,"hero_id":14,"item_0":1,"purchase_log":[{}]}}]}}"#, log.join(","));
    let (od, _dir) = service(build_api(detail, SIMPLE_ITEMS));
    let build = od.latest_hero_build("456", "pudge", "100", None).await;
    assert_eq!(build.items.len(), 20);
    assert_eq!(build.visible_items()[0].purchase_time_label(), "02:55");
    assert_eq!(build.overflow_items()[0].purchase_time_label(), "13:55");
    assert_eq!(build.overflow_label(), "+9");
    assert!(build.hero_match_label.contains("Purchase timeline"));
}

struct FakeReplay {
    positions: Option<HashMap<i64, i32>>,
    fail: bool,
    purchases: Option<Vec<(i32, i32)>>,
}

#[async_trait]
impl ReplaySource for FakeReplay {
    async fn analysis_outcome(&self, match_id: i64) -> Option<MatchAnalysisOutcome> {
        (match_id == 90 && !self.fail).then_some(MatchAnalysisOutcome::Stomp)
    }

    async fn positions(&self, match_id: i64) -> Result<Option<HashMap<i64, i32>>, String> {
        assert_eq!(match_id, 90);
        if self.fail {
            Err("STRATZ down".into())
        } else {
            Ok(self.positions.clone())
        }
    }

    async fn purchases(&self, match_id: i64, account_id: i64) -> Result<Option<Vec<(i32, i32)>>, String> {
        assert_eq!(match_id, 90);
        Ok(self.purchases.clone().filter(|_| account_id == 456))
    }
}

#[tokio::test]
async fn build_uses_stratz_purchase_times_when_opendota_has_no_purchase_log() {
    let items = r#"{"mithril_hammer":{"id":3,"dname":"Mithril Hammer","cost":1600},
        "black_king_bar":{"id":5,"dname":"Black King Bar","cost":4050,"components":["mithril_hammer","ogre_axe"]},
        "force_staff":{"id":7,"dname":"Force Staff","cost":2200,"components":["staff_of_wizardry","fluffy_hat"]}}"#;
    let (od, _dir) = service(build_api(r#"{"players":[{"account_id":456,"hero_id":14,"item_0":5}]}"#.into(), items));
    let replay = FakeReplay { positions: None, fail: false, purchases: Some(vec![(5, 1500), (999, 20), (7, 900)]) };

    let build = od.latest_hero_build("456", "pudge", "100", Some(&replay)).await;

    assert_eq!(build.items.iter().map(|i| i.name.as_str()).collect::<Vec<_>>(), ["Force Staff", "Black King Bar"]);
    assert_eq!(build.items.iter().map(|i| i.purchase_time_seconds).collect::<Vec<_>>(), [Some(900), Some(1500)]);
    assert!(build.hero_match_label.ends_with(" · Purchase timeline"));
    assert_eq!(build.analysis_outcome, Some(MatchAnalysisOutcome::Stomp));
}

#[tokio::test]
async fn latest_hero_match_shows_estimated_position_for_correct_player() {
    for (position, expected) in [
        ("1", "Pos 1 · Carry (estimated)"),
        ("2", "Pos 2 · Mid (estimated)"),
        ("3", "Pos 3 · Offlane (estimated)"),
        ("4", "Pos 4 · Soft support (estimated)"),
        ("null", "Position unavailable"),
        ("0", "Position unavailable"),
        ("5", "Pos 5 · Hard support (estimated)"),
        ("6", "Position unavailable"),
        ("\"2\"", "Position unavailable"),
    ] {
        let detail = format!(
            r#"{{"players":[{{"account_id":777,"hero_id":14,"position_est":2}},{{"account_id":456,"hero_id":1,"position_est":2}},
            {{"account_id":456,"hero_id":14,"lane_role":2,"position_est":{position}}}]}}"#
        );
        let (od, _dir) = service(build_api(detail, "{}"));
        let build = od.latest_hero_build("456", "pudge", "100", None).await;
        assert!(build.hero_match_label.contains(expected), "{position}: {}", build.hero_match_label);
        assert_eq!(build.position_label(), expected);
    }
}

#[tokio::test]
async fn stratz_position_replaces_farm_estimate_and_falls_back_when_missing() {
    for stratz_has_match in [true, false] {
        let http = FakeHttp::ok_or_404(|r| {
            Some(match path(r).as_str() {
                "/api/heroes" => r#"[{"id":20,"name":"npc_dota_hero_vengefulspirit","localized_name":"Vengeful Spirit"}]"#.into(),
                "/api/players/457/matches" => r#"[{"match_id":90,"hero_id":20,"start_time":1700000000,"player_slot":131}]"#.into(),
                "/api/matches/90" => r#"{"players":[
                    {"account_id":456,"hero_id":44,"player_slot":128,"gold_per_min":2046,"last_hits":513},
                    {"account_id":457,"hero_id":20,"player_slot":129,"gold_per_min":1650,"last_hits":312},
                    {"account_id":458,"hero_id":2,"player_slot":130,"gold_per_min":1571,"last_hits":208},
                    {"account_id":459,"hero_id":88,"player_slot":131,"gold_per_min":1302,"last_hits":146},
                    {"account_id":460,"hero_id":31,"player_slot":132,"gold_per_min":1209,"last_hits":96}]}"#
                    .into(),
                "/api/constants/items" => "{}".into(),
                other => panic!("unexpected {other}"),
            })
        });
        let (od, _dir) = service(http);
        let replay = FakeReplay {
            positions: stratz_has_match.then(|| HashMap::from([(457, 5), (460, 2)])),
            fail: false,
            purchases: None,
        };

        let build = od.latest_hero_build("457", "vengefulspirit", "100", Some(&replay)).await;

        assert_eq!(build.position, Some(if stratz_has_match { 5 } else { 2 }));
        assert_eq!(build.is_position_estimated, !stratz_has_match);
        assert_eq!(build.position_label(), if stratz_has_match { "Pos 5 · Hard support" } else { "Pos 2 · Mid (estimated)" });
        assert!(build.position_basis.contains(if stratz_has_match { "STRATZ" } else { "final team gold per minute" }));
        assert!(build.hero_match_label.contains(&build.position_label()));
    }
}

#[tokio::test]
async fn failing_position_source_falls_back_to_estimate() {
    let (od, _dir) = service(build_api(r#"{"players":[{"account_id":456,"hero_id":14,"position_est":3}]}"#.into(), "{}"));
    let replay = FakeReplay { positions: None, fail: true, purchases: None };
    let build = od.latest_hero_build("456", "pudge", "100", Some(&replay)).await;
    assert_eq!(build.position, Some(3));
    assert!(build.is_position_estimated);
}

#[tokio::test]
async fn missing_match_detail_still_links_latest_match_without_inventing_items() {
    let http = FakeHttp::ok_or_404(|r| match path(r).as_str() {
        "/api/heroes" => Some(PUDGE.into()),
        "/api/players/456/matches" => Some(r#"[{"match_id":90,"hero_id":14,"start_time":1700000000}]"#.into()),
        "/api/matches/90" => None,
        other => panic!("unexpected {other}"),
    });
    let (od, _dir) = service(http);
    let build = od.latest_hero_build("456", "pudge", "100", None).await;
    assert!(build.has_hero_match());
    assert!(build.items.is_empty());
    assert_eq!(build.position_label(), "Position unavailable");
    assert!(build.hero_match_label.contains("Item build unavailable"));
}

#[tokio::test]
async fn no_hero_and_self_do_not_make_network_requests() {
    let http = FakeHttp::ok_or_404(|r| panic!("unexpected request {}", r.url));
    let (od, _dir) = service(http.clone());
    assert_eq!(od.encounters("123", "123", "100").await, (vec![], "You".into()));
    let build = od.latest_hero_build("123", "", "100", None).await;
    assert!(!build.has_hero_match());
    assert_eq!(build.kda, "—");
    assert!(build.hero_match_label.contains("Choose a hero"));
    assert_eq!(http.count(), 0);
}

// ---- Cache behaviour ---------------------------------------------------------------------

#[tokio::test]
async fn reuses_player_history_across_matches_until_manually_refreshed() {
    let http = player_api(None, None, None);
    let (od, _dir) = service(http.clone());
    assert!(od.player_overview("118993939").await.is_some());
    let initial_requests = http.count();

    for match_id in ["100", "101"] {
        let (_, label) = od.recent_match_summaries("118993939", match_id, None).await;
        assert_eq!(label, "No recent matches recorded by OpenDota.");
        assert!(od.player_overview("118993939").await.is_some());
        assert_eq!(http.count(), initial_requests);
    }

    assert!(od.load_player_overview("118993939", true).await.is_some());
    assert_eq!(http.count() - initial_requests, 4);
}

#[tokio::test]
async fn transient_errors_retry_then_fall_back_to_stale_cache() {
    let http = FakeHttp::new(|_| Some((503, String::new())));
    let (od, dir) = service(http.clone());
    std::fs::write(dir.path().join("x.json"), r#"{"cached":true}"#).unwrap();
    let value = od.get_cached("x.json", "x", Some(Duration::ZERO)).await.unwrap();
    assert_eq!(value["cached"], true);
    assert_eq!(http.count(), 3, "three attempts on 503");

    let not_found = FakeHttp::new(|_| Some((404, String::new())));
    let (od, _dir) = service(not_found.clone());
    assert!(od.get_cached("y.json", "y", None).await.is_none());
    assert_eq!(not_found.count(), 1, "404 is not retried");
}

#[tokio::test]
async fn suggested_items_use_display_names_most_bought_first() {
    let http = FakeHttp::ok_or_404(|r| {
        Some(match path(r).as_str() {
            "/api/heroes" => PUDGE.into(),
            "/api/heroes/14/itemPopularity" => r#"{"start_game_items":{"1":5,"2":50},"early_game_items":{"2":9,"3":1}}"#.into(),
            "/api/constants/items" => r#"{"tango":{"id":1,"dname":"Tango"},"boots":{"id":2,"dname":"Boots of Speed"}}"#.into(),
            other => panic!("unexpected {other}"),
        })
    });
    let (od, _dir) = service(http);
    assert_eq!(od.suggested_items("npc_dota_hero_pudge").await, ["Boots of Speed", "Tango", "Item 3"]);
}
