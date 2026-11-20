//! Steam id conversions (C# `SteamIdConverter`).

const STEAM_ID64_OFFSET: u64 = 76_561_197_960_265_728;

/// Converts a GSI/OpenDota player identifier to SteamID64 for Steam Web API calls.
pub fn to_steam_id64(player_id: &str) -> Option<u64> {
    to_account_id(player_id).map(|id| id + STEAM_ID64_OFFSET)
}

/// Converts a GSI/OpenDota player identifier to an OpenDota (32-bit) account id.
/// GSI sends the 32-bit account id; notes and exports may use SteamID64.
pub fn to_account_id(player_id: &str) -> Option<u64> {
    let id: u64 = player_id.trim().parse().ok()?;
    if id > STEAM_ID64_OFFSET {
        Some(id - STEAM_ID64_OFFSET)
    } else if id > 0 && id <= u64::from(u32::MAX) {
        Some(id)
    } else {
        None
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn converts_steam_id64() {
        assert_eq!(to_account_id("76561198000000001"), Some(39_734_273));
    }

    #[test]
    fn accepts_gsi_account_id() {
        assert_eq!(to_account_id("87278757"), Some(87_278_757));
    }

    #[test]
    fn round_trips_account_id() {
        assert_eq!(to_steam_id64("39734273"), Some(76_561_198_000_000_001));
        assert_eq!(to_steam_id64("76561198000000001"), Some(76_561_198_000_000_001));
    }

    #[test]
    fn rejects_invalid_values() {
        for id in ["", "not-a-number", "0", "-5"] {
            assert_eq!(to_account_id(id), None, "{id}");
            assert_eq!(to_steam_id64(id), None, "{id}");
        }
    }
}
