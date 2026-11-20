//! Objective timers for the overlay (C# `ObjectiveTimerRules`, `TimerEntry`).

use serde::{Deserialize, Serialize};

const BOUNTY_INTERVAL: i32 = 240;
const POWER_INTERVAL: i32 = 120;
const WISDOM_INTERVAL: i32 = 420;
const LOTUS_INTERVAL: i32 = 180;
const TURBO_LOTUS_INTERVAL: i32 = 90;

/// Bounty runes are already on the map when the horn sounds.
pub fn next_bounty_rune(clock: i32) -> i32 {
    if clock < 0 {
        0
    } else {
        next_interval(clock, BOUNTY_INTERVAL)
    }
}

pub fn next_power_rune(clock: i32) -> i32 {
    next_interval(clock, POWER_INTERVAL)
}

pub fn next_wisdom_rune(clock: i32) -> i32 {
    next_interval(clock, WISDOM_INTERVAL)
}

pub fn next_lotus(clock: i32, turbo: bool) -> i32 {
    next_interval(clock, if turbo { TURBO_LOTUS_INTERVAL } else { LOTUS_INTERVAL })
}

/// Clock of the next multiple of `interval`, treating pre-horn time as 0:00.
fn next_interval(clock: i32, interval: i32) -> i32 {
    (clock.max(0) / interval + 1) * interval
}

/// `MM:SS`, with a leading `-` before the horn.
pub fn format_clock(clock_seconds: i32) -> String {
    let abs = i64::from(clock_seconds).abs();
    let sign = if clock_seconds < 0 { "-" } else { "" };
    format!("{sign}{:02}:{:02}", abs / 60, abs % 60)
}

#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct TimerEntry {
    pub name: String,
    pub seconds_until: Option<i32>,
    pub spawn_at_clock: Option<i32>,
}

impl TimerEntry {
    pub fn spawn(name: &str, seconds_until: i32, spawn_at_clock: i32) -> Self {
        Self {
            name: name.into(),
            seconds_until: Some(seconds_until),
            spawn_at_clock: Some(spawn_at_clock),
        }
    }

    /// `MM:SS` countdown until the next spawn, or empty when not applicable.
    pub fn countdown_display(&self) -> String {
        match self.seconds_until {
            Some(seconds) if seconds >= 0 => format_clock(seconds),
            _ => String::new(),
        }
    }

    /// True when a spawn countdown should be shown on the overlay.
    pub fn is_within_lead_window(&self, lead_seconds: i32) -> bool {
        matches!(self.seconds_until, Some(s) if (0..=lead_seconds).contains(&s))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn bounty_rune_spawns_every_four_minutes() {
        assert_eq!(next_bounty_rune(-30), 0);
        assert_eq!(next_bounty_rune(100), 240);
        assert_eq!(next_bounty_rune(240), 480);
    }

    #[test]
    fn power_rune_spawns_every_two_minutes() {
        assert_eq!(next_power_rune(0), 120);
        assert_eq!(next_power_rune(120), 240);
        assert_eq!(next_power_rune(300), 360);
    }

    #[test]
    fn wisdom_rune_spawns_every_seven_minutes() {
        assert_eq!(next_wisdom_rune(-10), 420);
        assert_eq!(next_wisdom_rune(420), 840);
    }

    #[test]
    fn lotus_halves_its_interval_in_turbo() {
        assert_eq!(next_lotus(100, false), 180);
        assert_eq!(next_lotus(180, false), 360);
        assert_eq!(next_lotus(0, true), 90);
        assert_eq!(next_lotus(90, true), 180);
    }

    #[test]
    fn clock_formatting() {
        assert_eq!(format_clock(754), "12:34");
        assert_eq!(format_clock(-45), "-00:45");
        assert_eq!(format_clock(0), "00:00");
    }

    #[test]
    fn timer_entry_lead_window() {
        let entry = TimerEntry::spawn("Power", 20, 360);
        assert_eq!(entry.countdown_display(), "00:20");
        assert!(entry.is_within_lead_window(30));
        assert!(!entry.is_within_lead_window(10));
        assert_eq!(TimerEntry::default().countdown_display(), "");
    }
}
