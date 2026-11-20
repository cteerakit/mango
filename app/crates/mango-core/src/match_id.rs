//! Match id helpers (C# `PlayerNotesMatch`). GSI reports `0` before a match has an id.

pub fn is_usable(match_id: &str) -> bool {
    let id = match_id.trim();
    !id.is_empty() && id != "0"
}

pub fn normalize(match_id: &str) -> String {
    let id = match_id.trim();
    if is_usable(id) {
        id.to_string()
    } else {
        String::new()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn zero_and_blank_are_not_usable() {
        assert!(!is_usable(""));
        assert!(!is_usable(" 0 "));
        assert!(is_usable(" 42 "));
        assert_eq!(normalize(" 42 "), "42");
        assert_eq!(normalize("0"), "");
    }
}
