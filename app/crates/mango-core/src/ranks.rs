//! Manual rank overrides for private or unranked profiles (C# `PlayerRanks`).

use indexmap::IndexMap;
use serde::{Deserialize, Serialize};

use crate::steam_id;

/// Saved in `settings.json` under `PlayerRanks`, in the WinUI app's PascalCase.
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase", default)]
pub struct PlayerRankOverride {
    #[serde(alias = "playerId")]
    pub player_id: String,
    #[serde(alias = "playerName")]
    pub player_name: String,
    #[serde(alias = "rankTier")]
    pub rank_tier: i32,
}

pub type RankOverrides = IndexMap<String, PlayerRankOverride>;

/// Herald 1 (11) through Divine 5 (75), plus Immortal (80).
pub fn tiers() -> impl Iterator<Item = i32> {
    (1..=7).flat_map(|medal| (1..=5).map(move |stars| medal * 10 + stars)).chain([80])
}

pub fn is_valid_tier(tier: i32) -> bool {
    tiers().any(|t| t == tier)
}

/// A saved rank applies only while the profile hides its rank or reports none.
pub fn can_override(is_profile_private: bool, reported_rank_tier: Option<i32>) -> bool {
    is_profile_private || !matches!(reported_rank_tier, Some(t) if t > 0)
}

pub fn get<'a>(
    ranks: &'a RankOverrides,
    player_id: &str,
    is_profile_private: bool,
    reported_rank_tier: Option<i32>,
) -> Option<&'a PlayerRankOverride> {
    if !can_override(is_profile_private, reported_rank_tier) {
        return None;
    }
    let key = steam_id::to_account_id(player_id)?.to_string();
    ranks.get(&key).filter(|rank| is_valid_tier(rank.rank_tier))
}

/// Re-keys by 32-bit account id and drops invalid ids or tiers.
pub fn normalize(ranks: Option<RankOverrides>) -> RankOverrides {
    let mut result = RankOverrides::new();
    for (id, rank) in ranks.unwrap_or_default() {
        if let Some(account) = steam_id::to_account_id(&id) {
            if is_valid_tier(rank.rank_tier) {
                result.insert(account.to_string(), rank);
            }
        }
    }
    result
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::model::MatchPlayer;

    fn saved(tier: i32) -> RankOverrides {
        RankOverrides::from([("12345".to_string(), PlayerRankOverride { rank_tier: tier, ..Default::default() })])
    }

    #[test]
    fn private_or_missing_reported_rank_can_edit_and_use_saved_ranks() {
        let ranks = saved(54);
        for (is_private, reported, expected) in [
            (true, Some(54), true),
            (true, None, true),
            (false, Some(54), false),
            (false, None, true),
            (false, Some(0), true),
        ] {
            let mut player = MatchPlayer {
                steam_id: "12345".into(),
                is_profile_private: is_private,
                reported_rank_tier: reported,
                ..Default::default()
            };
            assert_eq!(player.can_edit_rank(), expected);
            assert_eq!(get(&ranks, &player.steam_id, is_private, reported).is_some(), expected);
            player.steam_id.clear();
            assert!(!player.can_edit_rank());
        }
    }

    #[test]
    fn public_profile_stops_using_saved_rank_when_reported_rank_appears() {
        let ranks = saved(54);
        assert_eq!(get(&ranks, "12345", true, None).map(|r| r.rank_tier), Some(54));
        assert_eq!(get(&ranks, "12345", false, None).map(|r| r.rank_tier), Some(54));
        assert!(get(&ranks, "12345", false, Some(63)).is_none());
    }

    #[test]
    fn saved_rank_survives_serialization_and_matches_either_player_id_format() {
        let ranks = RankOverrides::from([(
            "12345".to_string(),
            PlayerRankOverride { player_id: "12345".into(), player_name: "Private player".into(), rank_tier: 54 },
        )]);
        let json = serde_json::to_string(&ranks).unwrap();
        assert!(json.contains("\"RankTier\":54"), "PascalCase on disk: {json}");
        let mut restored = normalize(serde_json::from_str(&json).unwrap());

        assert_eq!(get(&restored, "76561197960278073", true, None).map(|r| r.rank_tier), Some(54));
        assert_eq!(get(&restored, "12345", true, None).map(|r| r.player_name.as_str()), Some("Private player"));
        assert!(get(&restored, "67890", true, None).is_none());

        restored.shift_remove("12345");
        let cleared = normalize(serde_json::from_str(&serde_json::to_string(&restored).unwrap()).unwrap());
        assert!(get(&cleared, "76561197960278073", true, None).is_none());
    }

    #[test]
    fn normalize_converts_steam_ids_and_rejects_invalid_ranks_and_players() {
        let input = RankOverrides::from_iter([
            ("76561197960278073".to_string(), PlayerRankOverride { rank_tier: 80, ..Default::default() }),
            ("23456".to_string(), PlayerRankOverride { rank_tier: 76, ..Default::default() }),
            ("34567".to_string(), PlayerRankOverride { rank_tier: 0, ..Default::default() }),
            ("unknown".to_string(), PlayerRankOverride { rank_tier: 35, ..Default::default() }),
        ]);
        let ranks = normalize(Some(input));
        assert_eq!(ranks.len(), 1);
        assert_eq!(ranks["12345"].rank_tier, 80);
        assert!(get(&ranks, "", true, None).is_none());
        assert!(normalize(None).is_empty());
    }

    #[test]
    fn reads_camel_case_too() {
        let rank: PlayerRankOverride = serde_json::from_str(r#"{"rankTier":21,"playerId":"5"}"#).unwrap();
        assert_eq!(rank.rank_tier, 21);
        assert_eq!(rank.player_id, "5");
    }
}
