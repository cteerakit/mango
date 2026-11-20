//! Per-player match history shown on the roster (C# `PlayerMatchHistory`).

use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};

/// Items beyond this many go into the "+N" overflow.
pub const VISIBLE_ITEM_COUNT: usize = 11;

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct PlayerMatchHistory {
    pub kda: String,
    pub result: String,
    pub position: Option<i32>,
    pub position_basis: String,
    /// False when the position comes from a replay-analysed source such as STRATZ.
    pub is_position_estimated: bool,
    pub played_at: Option<DateTime<Utc>>,
    pub encounter_label: String,
    pub hero_match_label: String,
    pub hero_match_url: String,
    pub items: Vec<MatchBuildItem>,
    pub recent_matches: Vec<RecentMatchSummary>,
    pub recent_matches_label: String,
}

impl Default for PlayerMatchHistory {
    fn default() -> Self {
        Self {
            kda: "—".into(),
            result: String::new(),
            position: None,
            position_basis: "OpenDota parsed-match estimate.".into(),
            is_position_estimated: true,
            played_at: None,
            encounter_label: "Loading past encounters…".into(),
            hero_match_label: "Loading latest hero match…".into(),
            hero_match_url: String::new(),
            items: Vec::new(),
            recent_matches: Vec::new(),
            recent_matches_label: "Loading recent matches…".into(),
        }
    }
}

impl PlayerMatchHistory {
    pub fn position_label(&self) -> String {
        format_position_label(self.position, self.is_position_estimated)
    }

    pub fn has_hero_match(&self) -> bool {
        !self.hero_match_url.is_empty()
    }

    pub fn visible_items(&self) -> &[MatchBuildItem] {
        &self.items[..self.items.len().min(VISIBLE_ITEM_COUNT)]
    }

    pub fn overflow_items(&self) -> &[MatchBuildItem] {
        &self.items[self.items.len().min(VISIBLE_ITEM_COUNT)..]
    }

    pub fn overflow_label(&self) -> String {
        format!("+{}", self.items.len().saturating_sub(VISIBLE_ITEM_COUNT))
    }

    pub fn result_kda(&self) -> String {
        if self.result.is_empty() {
            self.kda.clone()
        } else {
            format!("{} {}", self.result, self.kda)
        }
    }
}

pub fn format_position_label(position: Option<i32>, estimated: bool) -> String {
    let role = match position {
        Some(1) => "Pos 1 · Carry",
        Some(2) => "Pos 2 · Mid",
        Some(3) => "Pos 3 · Offlane",
        Some(4) => "Pos 4 · Soft support",
        Some(5) => "Pos 5 · Hard support",
        _ => return "Position unavailable".into(),
    };
    if estimated {
        format!("{role} (estimated)")
    } else {
        role.into()
    }
}

pub fn format_relative_time(played_at: DateTime<Utc>, now: DateTime<Utc>) -> String {
    let seconds = (now - played_at).num_milliseconds() as f64 / 1000.0;
    let minutes = seconds / 60.0;
    let hours = minutes / 60.0;
    let days = hours / 24.0;
    if minutes < 1.0 {
        "Just now".into()
    } else if hours < 1.0 {
        ago(minutes as i64, "minute")
    } else if days < 1.0 {
        ago(hours as i64, "hour")
    } else if days < 30.0 {
        ago(days as i64, "day")
    } else if days < 365.0 {
        ago((days / 30.0) as i64, "month")
    } else {
        ago((days / 365.0) as i64, "year")
    }
}

fn ago(count: i64, unit: &str) -> String {
    format!("{count} {unit}{} ago", if count == 1 { "" } else { "s" })
}

#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct RecentMatchSummary {
    pub hero_name: String,
    pub hero_image_url: String,
    pub result: String,
}

#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct MatchBuildItem {
    pub name: String,
    pub image_url: String,
    pub slot: String,
    pub purchase_time_seconds: Option<i32>,
}

impl MatchBuildItem {
    /// Match clock of the purchase; pre-horn purchases are negative (U+2212 minus sign).
    pub fn purchase_time_label(&self) -> String {
        match self.purchase_time_seconds {
            None => "—".into(),
            Some(seconds) => {
                let abs = i64::from(seconds).abs();
                let sign = if seconds < 0 { "−" } else { "" };
                format!("{sign}{:02}:{:02}", abs / 60, abs % 60)
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use chrono::TimeZone;

    #[test]
    fn purchase_time_uses_match_minutes_and_seconds() {
        for (seconds, label) in [(None, "—"), (Some(-65), "−01:05"), (Some(0), "00:00"), (Some(3601), "60:01")] {
            let item = MatchBuildItem { purchase_time_seconds: seconds, ..Default::default() };
            assert_eq!(item.purchase_time_label(), label);
        }
    }

    #[test]
    fn match_timestamp_uses_relative_time() {
        let now = Utc.timestamp_opt(1_800_000_000, 0).unwrap();
        for (seconds_ago, expected) in [
            (0, "Just now"),
            (60, "1 minute ago"),
            (120, "2 minutes ago"),
            (3600, "1 hour ago"),
            (7200, "2 hours ago"),
            (86400, "1 day ago"),
            (172_800, "2 days ago"),
            (2_592_000, "1 month ago"),
            (31_536_000, "1 year ago"),
        ] {
            let played = now - chrono::Duration::seconds(seconds_ago);
            assert_eq!(format_relative_time(played, now), expected);
        }
    }

    #[test]
    fn overflow_splits_after_eleven_items() {
        let history = PlayerMatchHistory {
            items: (0..20).map(|_| MatchBuildItem::default()).collect(),
            ..Default::default()
        };
        assert_eq!(history.visible_items().len(), 11);
        assert_eq!(history.overflow_items().len(), 9);
        assert_eq!(history.overflow_label(), "+9");
    }

    #[test]
    fn position_labels() {
        assert_eq!(format_position_label(Some(1), true), "Pos 1 · Carry (estimated)");
        assert_eq!(format_position_label(Some(5), false), "Pos 5 · Hard support");
        assert_eq!(format_position_label(Some(6), true), "Position unavailable");
    }
}
