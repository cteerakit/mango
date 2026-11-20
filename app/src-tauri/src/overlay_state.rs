//! What the overlay draws each second (C# `TimerService`, `OverlayViewModel`).

use std::collections::HashSet;
use std::time::Instant;

use mango_core::model::MatchSnapshot;
use mango_core::timers::{self, TimerEntry};
use serde::Serialize;

use crate::settings::{AppSettings, PanelLayout};

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct TimerView {
    /// `bounty`, `power`, `wisdom`, `lotus` — also the widget and icon id.
    pub id: &'static str,
    pub name: String,
    pub countdown: String,
    pub seconds_until: Option<i32>,
    pub visible: bool,
}

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OverlayView {
    pub gpm: i32,
    pub xpm: i32,
    pub interactive: bool,
    pub opacity: f64,
    pub show_player_panel: bool,
    pub show_build_panel: bool,
    pub layout: LayoutView,
    pub timers: Vec<TimerView>,
    pub build_items: Vec<String>,
    /// Set once per spawn when timer alerts are on, e.g. `Power Rune in 5s`.
    pub alert: Option<String>,
    pub toggle_interactive_hint: String,
}

/// Saved widget positions for the overlay page (camelCase for JS; `None` = default anchor).
#[derive(Debug, Clone, Copy, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct LayoutView {
    pub player: Option<(f64, f64)>,
    pub bounty: Option<(f64, f64)>,
    pub power: Option<(f64, f64)>,
    pub wisdom: Option<(f64, f64)>,
    pub lotus: Option<(f64, f64)>,
    pub build: Option<(f64, f64)>,
}

impl From<&PanelLayout> for LayoutView {
    fn from(l: &PanelLayout) -> Self {
        let pair = |x: Option<f64>, y: Option<f64>| x.zip(y);
        Self {
            player: pair(l.player_x, l.player_y),
            bounty: pair(l.bounty_x, l.bounty_y),
            power: pair(l.power_x, l.power_y),
            wisdom: pair(l.wisdom_x, l.wisdom_y),
            lotus: pair(l.lotus_x, l.lotus_y),
            build: pair(l.build_x, l.build_y),
        }
    }
}

/// Stores a dragged position. Unknown widget ids are ignored.
pub fn set_position(layout: &mut PanelLayout, widget: &str, x: f64, y: f64) {
    let slot = match widget {
        "player" => (&mut layout.player_x, &mut layout.player_y),
        "bounty" => (&mut layout.bounty_x, &mut layout.bounty_y),
        "power" => (&mut layout.power_x, &mut layout.power_y),
        "wisdom" => (&mut layout.wisdom_x, &mut layout.wisdom_y),
        "lotus" => (&mut layout.lotus_x, &mut layout.lotus_y),
        "build" => (&mut layout.build_x, &mut layout.build_y),
        _ => return,
    };
    *slot.0 = Some(x.max(0.0));
    *slot.1 = Some(y.max(0.0));
}

/// Game clock between GSI updates, which arrive about once a second.
#[derive(Default)]
pub struct Clock {
    last_clock: i32,
    last_at: Option<Instant>,
    fired_alerts: HashSet<String>,
}

impl Clock {
    pub fn observe(&mut self, snapshot: &MatchSnapshot) {
        self.last_clock = snapshot.clock_time_seconds;
        self.last_at = Some(Instant::now());
    }

    pub fn current(&self) -> i32 {
        match self.last_at {
            Some(at) => self.last_clock + at.elapsed().as_secs() as i32,
            None => self.last_clock,
        }
    }

    pub fn build_view(
        &mut self,
        snapshot: &MatchSnapshot,
        settings: &AppSettings,
        is_mock: bool,
        build_items: Vec<String>,
        toggle_interactive_hint: String,
    ) -> OverlayView {
        let clock = self.current();
        let interactive = settings.overlay_interactive;
        let in_match = is_mock || snapshot.is_in_match();
        let lead = settings.rune_timer_lead_seconds;
        let entries = [
            ("bounty", "Bounty Rune", timers::next_bounty_rune(clock), settings.show_bounty_timer),
            ("power", "Power Rune", timers::next_power_rune(clock), settings.show_power_timer),
            ("wisdom", "Wisdom Rune", timers::next_wisdom_rune(clock), settings.show_wisdom_timer),
            ("lotus", "Lotus Pool", timers::next_lotus(clock, settings.is_turbo_mode), settings.show_lotus_timer),
        ];

        let mut alert = None;
        let timers = entries
            .into_iter()
            .map(|(id, name, spawn_at, enabled)| {
                let entry = TimerEntry::spawn(name, spawn_at - clock, spawn_at);
                let seconds = spawn_at - clock;
                if settings.timer_sounds_enabled && in_match && (0..=5).contains(&seconds) {
                    let key = format!("{name}:{spawn_at}");
                    if self.fired_alerts.insert(key) {
                        alert = Some(format!("{name} in {seconds}s"));
                    }
                }
                TimerView {
                    id,
                    name: name.into(),
                    countdown: entry.countdown_display(),
                    seconds_until: entry.seconds_until,
                    visible: enabled && (interactive || (in_match && entry.is_within_lead_window(lead))),
                }
            })
            .collect();

        OverlayView {
            gpm: snapshot.gpm,
            xpm: snapshot.xpm,
            interactive,
            opacity: settings.overlay_opacity,
            show_player_panel: settings.show_player_panel,
            show_build_panel: settings.show_build_panel,
            layout: LayoutView::from(&settings.panel_layout),
            timers,
            build_items,
            alert,
            toggle_interactive_hint,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn in_game(clock: i32) -> MatchSnapshot {
        MatchSnapshot { clock_time_seconds: clock, game_state: "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS".into(), gpm: 500, ..Default::default() }
    }

    #[test]
    fn rune_widgets_appear_inside_the_lead_window() {
        let mut clock = Clock::default();
        clock.observe(&in_game(110)); // power at 120 in 10 s, bounty at 240
        let view = clock.build_view(&in_game(110), &AppSettings::default(), false, vec![], String::new());
        let power = view.timers.iter().find(|t| t.id == "power").unwrap();
        let bounty = view.timers.iter().find(|t| t.id == "bounty").unwrap();
        assert!(power.visible);
        assert_eq!(power.countdown, "00:10");
        assert!(!bounty.visible);
        assert_eq!(view.gpm, 500);
    }

    #[test]
    fn interactive_mode_shows_every_enabled_timer() {
        let mut clock = Clock::default();
        let settings = AppSettings { overlay_interactive: true, show_wisdom_timer: false, ..Default::default() };
        let view = clock.build_view(&in_game(10), &settings, false, vec![], String::new());
        let visible: Vec<_> = view.timers.iter().filter(|t| t.visible).map(|t| t.id).collect();
        assert_eq!(visible, ["bounty", "power", "lotus"]);
    }

    #[test]
    fn alerts_fire_once_per_spawn() {
        let mut clock = Clock::default();
        clock.observe(&in_game(116)); // power in 4 s
        let settings = AppSettings::default();
        let first = clock.build_view(&in_game(116), &settings, false, vec![], String::new());
        assert_eq!(first.alert.as_deref(), Some("Power Rune in 4s"));
        let second = clock.build_view(&in_game(116), &settings, false, vec![], String::new());
        assert_eq!(second.alert, None);
        let quiet = AppSettings { timer_sounds_enabled: false, ..Default::default() };
        let mut other = Clock::default();
        other.observe(&in_game(116));
        assert_eq!(other.build_view(&in_game(116), &quiet, false, vec![], String::new()).alert, None);
    }

    #[test]
    fn menus_show_no_countdowns() {
        let mut clock = Clock::default();
        let view = clock.build_view(&MatchSnapshot::default(), &AppSettings::default(), false, vec![], String::new());
        assert!(view.timers.iter().all(|t| !t.visible));
    }

    #[test]
    fn positions_are_saved_per_widget() {
        let mut layout = PanelLayout::default();
        set_position(&mut layout, "power", 12.0, -4.0);
        set_position(&mut layout, "nope", 1.0, 1.0);
        assert_eq!((layout.power_x, layout.power_y), (Some(12.0), Some(0.0)));
        assert_eq!(LayoutView::from(&layout).power, Some((12.0, 0.0)));
        assert_eq!(LayoutView::from(&layout).bounty, None);
    }
}
