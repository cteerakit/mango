//! Which match the Match page shows (C# `MatchPageState`).
//!
//! A finished match stays visible through the post-game screen, the main menu and a
//! disconnect, until the next match starts.

use crate::match_id;
use crate::model::MatchSnapshot;

const POST_GAME: &str = "DOTA_GAMERULES_STATE_POST_GAME";

#[derive(Debug, Default, Clone)]
pub struct MatchPageState {
    snapshot: MatchSnapshot,
    is_previous_match: bool,
}

impl MatchPageState {
    pub fn snapshot(&self) -> &MatchSnapshot {
        &self.snapshot
    }

    /// True when the page shows a match that has ended.
    pub fn is_previous_match(&self) -> bool {
        self.is_previous_match
    }

    pub fn update(&mut self, incoming: MatchSnapshot) {
        let is_active = incoming.is_connected && incoming.is_in_match();
        let same_match =
            match_id::is_usable(&incoming.match_id) && incoming.match_id == self.snapshot.match_id;

        if is_active {
            if !same_match || incoming.players.len() >= self.snapshot.players.len() {
                self.snapshot = incoming;
            }
            self.is_previous_match = false;
            return;
        }

        let has_match = self.has_match();
        let final_roster = incoming.game_state == POST_GAME
            && match_id::is_usable(&incoming.match_id)
            && !incoming.players.is_empty()
            && (!same_match || incoming.players.len() >= self.snapshot.players.len());
        if !has_match || final_roster {
            self.snapshot = incoming;
        }

        self.is_previous_match = self.has_match();
    }

    fn has_match(&self) -> bool {
        match_id::is_usable(&self.snapshot.match_id) && !self.snapshot.players.is_empty()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::model::MatchPlayer;

    fn in_match(id: &str) -> MatchSnapshot {
        MatchSnapshot {
            is_connected: true,
            match_id: id.into(),
            game_state: "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS".into(),
            players: (1..=10)
                .map(|i| MatchPlayer { steam_id: i.to_string(), name: format!("Player {i}"), ..Default::default() })
                .collect(),
            ..Default::default()
        }
    }

    fn connected(match_id: &str, game_state: &str) -> MatchSnapshot {
        MatchSnapshot {
            is_connected: true,
            match_id: match_id.into(),
            game_state: game_state.into(),
            ..Default::default()
        }
    }

    #[test]
    fn finished_match_remains_visible_through_post_game_menu_and_disconnect() {
        let mut state = MatchPageState::default();
        let game = in_match("100");
        state.update(game.clone());
        state.update(connected("100", POST_GAME));
        state.update(connected("", "DOTA_GAMERULES_STATE_DISCONNECT"));
        state.update(MatchSnapshot::default());

        assert_eq!(state.snapshot(), &game);
        assert_eq!(state.snapshot().players.len(), 10);
        assert!(state.is_previous_match());
    }

    #[test]
    fn new_match_replaces_previous_roster() {
        for (id, phase) in [
            ("", "DOTA_GAMERULES_STATE_HERO_SELECTION"),
            ("200", "DOTA_GAMERULES_STATE_WAIT_FOR_PLAYERS_TO_LOAD"),
            ("200", "DOTA_GAMERULES_STATE_PRE_GAME"),
            ("200", "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS"),
        ] {
            let mut state = MatchPageState::default();
            state.update(in_match("100"));
            state.update(connected("", ""));
            let next = connected(id, phase);
            state.update(next.clone());

            assert_eq!(state.snapshot(), &next, "{phase}");
            assert!(state.snapshot().players.is_empty());
            assert!(!state.is_previous_match());
        }
    }

    #[test]
    fn sparse_post_game_retains_players_until_final_roster_arrives() {
        let mut state = MatchPageState::default();
        state.update(in_match("100"));
        state.update(connected("100", POST_GAME));
        assert_eq!(state.snapshot().players.len(), 10);
        assert!(state.is_previous_match());

        let mut final_roster = in_match("100");
        final_roster.game_state = POST_GAME.into();
        final_roster.players[0].kills = 7;
        state.update(final_roster.clone());
        assert_eq!(state.snapshot(), &final_roster);
    }
}
