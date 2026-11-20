//! Display medal for a player (C# `RankEstimator`, `OpenDotaService.FormatRankTier`).
//!
//! Resolves from confirmed `rank_tier`, teammate ranks in recent ranked matches, a legacy
//! OpenDota MMR estimate, or the current lobby average. MMR bands follow the in-game seasonal
//! table: 154 MMR per star through Ancient, 200 per star in Divine, Immortal from 5620.

use serde::Serialize;

use crate::ranks;

const STAR_WIDTH_MMR: f64 = 154.0;
const DIVINE_FLOOR_MMR: f64 = 4620.0;
const DIVINE_STAR_WIDTH_MMR: f64 = 200.0;
const IMMORTAL_FLOOR_MMR: f64 = 5620.0;
const RANKED_LOBBY_TYPE: i32 = 7;
const TEAM_SIZE: usize = 5;
const MAX_ESTIMATED_MMR: f64 = 8000.0;

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ResolvedRank {
    pub rank_tier: Option<i32>,
    pub label: String,
    pub is_estimated: bool,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RankEstimate {
    pub rank_tier: i32,
    pub mmr: i32,
    pub match_count: usize,
}

/// One of the player's recent matches, with the slots and ranks of everyone in it.
#[derive(Debug, Clone, Default)]
pub struct RankedMatchSample {
    pub lobby_type: i32,
    pub player_slot: i32,
    pub average_rank: Option<i32>,
    /// `(player_slot, rank_tier)`; `None` when the match detail is unavailable.
    pub players: Option<Vec<(i32, Option<i32>)>>,
}

pub fn format_rank_tier(rank_tier: Option<i32>, leaderboard_rank: Option<i32>) -> String {
    let Some(tier) = rank_tier.filter(|t| *t > 0) else {
        return "Unranked".into();
    };
    let (medal, stars) = (tier / 10, tier % 10);
    let name = match medal {
        1 => "Herald",
        2 => "Guardian",
        3 => "Crusader",
        4 => "Archon",
        5 => "Legend",
        6 => "Ancient",
        7 => "Divine",
        8 => "Immortal",
        _ => "Unknown",
    };
    if medal == 8 {
        return match leaderboard_rank {
            Some(rank) if rank > 0 => format!("Immortal #{rank}"),
            _ => name.into(),
        };
    }
    if stars > 0 {
        format!("{name} {stars}")
    } else {
        name.into()
    }
}

pub fn resolve(
    rank_tier: Option<i32>,
    leaderboard_rank: Option<i32>,
    history_estimate: Option<RankEstimate>,
    legacy_mmr_estimate: Option<i32>,
    lobby_average_tiers: Option<i32>,
    is_profile_private: bool,
) -> ResolvedRank {
    if let Some(tier) = rank_tier.filter(|t| *t > 0) {
        if !is_profile_private || history_estimate.is_none() {
            let label = if is_profile_private {
                format!(
                    "Estimated: {} (last reported rank; private profile, current rank unverified)",
                    format_rank_tier(Some(tier), None)
                )
            } else {
                format_rank_tier(Some(tier), leaderboard_rank)
            };
            return ResolvedRank { rank_tier: Some(tier), label, is_estimated: is_profile_private };
        }
    }

    if let Some(history) = history_estimate {
        let noun = if history.match_count == 1 { "match" } else { "matches" };
        return ResolvedRank {
            rank_tier: Some(history.rank_tier),
            label: format!(
                "Estimated: {} (~{} MMR from teammates' ranks in {} recent ranked {noun})",
                format_rank_tier(Some(history.rank_tier), None),
                thousands(i64::from(history.mmr)),
                history.match_count
            ),
            is_estimated: true,
        };
    }

    if let Some(mmr) = legacy_mmr_estimate.filter(|m| *m > 0) {
        if let Some(tier) = rank_tier_from_mmr(f64::from(mmr)).filter(|t| *t > 0) {
            return ResolvedRank {
                rank_tier: Some(tier),
                label: format!("Estimated: {} (~{} MMR)", format_rank_tier(Some(tier), None), thousands(i64::from(mmr))),
                is_estimated: true,
            };
        }
    }

    if let Some(tier) = lobby_average_tiers.filter(|t| *t > 0) {
        return ResolvedRank {
            rank_tier: Some(tier),
            label: format!("Lobby avg: ~{}", format_rank_tier(Some(tier), None)),
            is_estimated: true,
        };
    }

    ResolvedRank { rank_tier: None, label: String::new(), is_estimated: false }
}

/// Estimates the player's MMR from recent ranked matches, assuming both teams average the lobby
/// rank Valve reports: the player's team totals five times the lobby MMR, so subtracting the
/// known teammates leaves the player's share. Unranked teammates split that remainder evenly
/// with the player. Matches combine with a median weighted by how many teammates were known.
pub fn estimate_from_teammate_ranks(matches: &[RankedMatchSample]) -> Option<RankEstimate> {
    let mut samples: Vec<(f64, usize)> = Vec::new();
    for m in matches {
        let (Some(players), Some(lobby_tier)) = (&m.players, m.average_rank) else { continue };
        if m.lobby_type != RANKED_LOBBY_TYPE || !ranks::is_valid_tier(lobby_tier) {
            continue;
        }
        let is_radiant = m.player_slot < 128;
        let known: Vec<f64> = players
            .iter()
            .filter(|(slot, _)| *slot != m.player_slot && (*slot < 128) == is_radiant)
            .filter_map(|(_, tier)| tier.filter(|t| ranks::is_valid_tier(*t)).and_then(approximate_mmr_from_rank_tier))
            .take(TEAM_SIZE - 1)
            .collect();
        if known.is_empty() {
            continue;
        }
        let team_mmr = TEAM_SIZE as f64 * approximate_mmr_from_rank_tier(lobby_tier)?;
        let share = (team_mmr - known.iter().sum::<f64>()) / (TEAM_SIZE - known.len()) as f64;
        samples.push((share.clamp(0.0, MAX_ESTIMATED_MMR), known.len()));
    }
    if samples.is_empty() {
        return None;
    }
    let mmr = weighted_median(&mut samples);
    Some(RankEstimate {
        rank_tier: rank_tier_from_mmr(mmr)?,
        mmr: mmr.round() as i32,
        match_count: samples.len(),
    })
}

fn weighted_median(samples: &mut [(f64, usize)]) -> f64 {
    samples.sort_by(|a, b| a.0.total_cmp(&b.0));
    let half = samples.iter().map(|s| s.1).sum::<usize>() as f64 / 2.0;
    let mut cumulative = 0.0;
    for i in 0..samples.len() {
        cumulative += samples[i].1 as f64;
        if cumulative > half {
            return samples[i].0;
        }
        if cumulative == half {
            return (samples[i].0 + samples[i + 1].0) / 2.0;
        }
    }
    samples[samples.len() - 1].0
}

pub fn average_rank_tier(confirmed_tiers: impl IntoIterator<Item = i32>) -> Option<i32> {
    let mmrs: Vec<f64> =
        confirmed_tiers.into_iter().filter(|t| *t > 0).filter_map(approximate_mmr_from_rank_tier).collect();
    if mmrs.is_empty() {
        return None;
    }
    rank_tier_from_mmr(mmrs.iter().sum::<f64>() / mmrs.len() as f64)
}

pub fn rank_tier_from_mmr(mmr: f64) -> Option<i32> {
    if mmr < 0.0 {
        return None;
    }
    if mmr >= IMMORTAL_FLOOR_MMR {
        return Some(80);
    }
    if mmr >= DIVINE_FLOOR_MMR {
        let stars = (((mmr - DIVINE_FLOOR_MMR) / DIVINE_STAR_WIDTH_MMR) as i32 + 1).clamp(1, 5);
        return Some(70 + stars);
    }
    let step = (mmr / STAR_WIDTH_MMR) as i32;
    Some((step / 5 + 1) * 10 + step % 5 + 1)
}

pub fn approximate_mmr_from_rank_tier(rank_tier: i32) -> Option<f64> {
    let medal = rank_tier / 10;
    let stars = rank_tier % 10;
    if medal <= 0 {
        return None;
    }
    if medal >= 8 {
        return Some(IMMORTAL_FLOOR_MMR + 300.0);
    }
    let stars = f64::from(if stars <= 0 { 1 } else { stars }.clamp(1, 5));
    if medal == 7 {
        return Some(DIVINE_FLOOR_MMR + (stars - 1.0) * DIVINE_STAR_WIDTH_MMR + DIVINE_STAR_WIDTH_MMR / 2.0);
    }
    Some((f64::from((medal - 1) * 5) + stars - 1.0) * STAR_WIDTH_MMR + STAR_WIDTH_MMR / 2.0)
}

/// `N0` formatting: `3,200`.
pub fn thousands(n: i64) -> String {
    let digits = n.unsigned_abs().to_string();
    let mut out = String::new();
    for (i, c) in digits.chars().enumerate() {
        if i > 0 && (digits.len() - i).is_multiple_of(3) {
            out.push(',');
        }
        out.push(c);
    }
    if n < 0 {
        format!("-{out}")
    } else {
        out
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rank_tier_from_mmr_uses_seasonal_bands() {
        for (mmr, tier) in [
            (0, 11),
            (153, 11),
            (154, 12),
            (770, 21),
            (3080, 51),
            (4619, 65),
            (4620, 71),
            (4820, 72),
            (5420, 75),
            (5619, 75),
            (5620, 80),
        ] {
            assert_eq!(rank_tier_from_mmr(f64::from(mmr)), Some(tier), "{mmr}");
        }
    }

    #[test]
    fn approximate_mmr_round_trips_every_tier() {
        for tier in ranks::tiers() {
            assert_eq!(rank_tier_from_mmr(approximate_mmr_from_rank_tier(tier).unwrap()), Some(tier));
        }
    }

    fn sample(lobby_tier: i32, radiant_teammates: &[Option<i32>]) -> RankedMatchSample {
        let mut players = vec![(0, None)];
        players.extend(radiant_teammates.iter().enumerate().map(|(i, t)| (i as i32 + 1, *t)));
        players.extend((128..133).map(|slot| (slot, Some(11))));
        RankedMatchSample { lobby_type: 7, player_slot: 0, average_rank: Some(lobby_tier), players: Some(players) }
    }

    fn mmr(tier: i32) -> f64 {
        approximate_mmr_from_rank_tier(tier).unwrap()
    }

    #[test]
    fn fully_known_team_leaves_the_players_share_of_the_lobby_total() {
        let estimate = estimate_from_teammate_ranks(&[sample(61, &[Some(51); 4])]).unwrap();
        assert_eq!(Some(estimate.rank_tier), rank_tier_from_mmr(5.0 * mmr(61) - 4.0 * mmr(51)));
        assert!(estimate.rank_tier > 61);
        assert_eq!(estimate.match_count, 1);
    }

    #[test]
    fn unranked_teammates_split_the_remainder_and_enemies_are_ignored() {
        let estimate = estimate_from_teammate_ranks(&[sample(61, &[Some(71), Some(71), None, None])]).unwrap();
        assert_eq!(estimate.mmr, ((5.0 * mmr(61) - 2.0 * mmr(71)) / 3.0).round() as i32);
    }

    #[test]
    fn median_ignores_an_outlier_match() {
        let estimate = estimate_from_teammate_ranks(&[
            sample(52, &[Some(52); 4]),
            sample(52, &[Some(52); 4]),
            sample(52, &[Some(11); 4]),
        ])
        .unwrap();
        assert_eq!(estimate.rank_tier, 52);
        assert_eq!(estimate.match_count, 3);
    }

    #[test]
    fn matches_without_known_teammates_or_lobby_rank_are_skipped() {
        assert!(estimate_from_teammate_ranks(&[sample(52, &[None; 4])]).is_none());
        let mut no_detail = sample(52, &[Some(52); 4]);
        no_detail.players = None;
        assert!(estimate_from_teammate_ranks(&[no_detail]).is_none());
        let mut no_lobby = sample(52, &[Some(52); 4]);
        no_lobby.average_rank = None;
        assert!(estimate_from_teammate_ranks(&[no_lobby]).is_none());
        assert!(estimate_from_teammate_ranks(&[]).is_none());
    }

    #[test]
    fn resolve_prefers_confirmed_tier_then_history_then_lobby_average() {
        let history = RankEstimate { rank_tier: 52, mmr: 3200, match_count: 8 };
        let confirmed = resolve(Some(63), None, Some(history), None, Some(41), false);
        assert!(!confirmed.is_estimated);
        assert_eq!(confirmed.rank_tier, Some(63));

        let from_history = resolve(None, None, Some(history), Some(5000), Some(41), false);
        assert_eq!(from_history.rank_tier, Some(52));
        assert!(from_history.is_estimated);
        assert_eq!(
            from_history.label,
            "Estimated: Legend 2 (~3,200 MMR from teammates' ranks in 8 recent ranked matches)"
        );
        assert_eq!(resolve(None, None, None, Some(4900), Some(41), false).rank_tier, Some(72));
        assert_eq!(resolve(None, None, None, None, Some(41), false).rank_tier, Some(41));
    }

    #[test]
    fn private_reported_rank_is_unverified() {
        let r = resolve(Some(63), Some(123), None, None, None, true);
        assert!(r.is_estimated);
        assert!(r.label.contains("private profile"));
        assert!(!r.label.contains("#123"));
        assert_eq!(format_rank_tier(Some(80), Some(123)), "Immortal #123");
        assert_eq!(format_rank_tier(None, None), "Unranked");
    }

    #[test]
    fn thousands_groups_digits() {
        assert_eq!(thousands(0), "0");
        assert_eq!(thousands(1234567), "1,234,567");
        assert_eq!(thousands(-4210), "-4,210");
    }
}
