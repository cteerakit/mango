//! String helpers matching the .NET semantics the C# code relied on.

use std::cmp::Ordering;

/// `string.IsNullOrWhiteSpace`.
pub fn is_blank(value: &str) -> bool {
    value.trim().is_empty()
}

/// `StringComparison.OrdinalIgnoreCase` equality.
pub fn eq_ignore_case(a: &str, b: &str) -> bool {
    a == b || fold(a) == fold(b)
}

/// `StringComparer.OrdinalIgnoreCase` ordering.
pub fn cmp_ignore_case(a: &str, b: &str) -> Ordering {
    fold(a).cmp(&fold(b))
}

pub fn contains_ignore_case(haystack: &str, needle: &str) -> bool {
    fold(haystack).contains(&fold(needle))
}

/// Case-folded form used as the key of case-insensitive maps.
pub fn fold(value: &str) -> String {
    value.to_uppercase()
}

/// Replaces every ASCII-case-insensitive occurrence of `pattern` (which must be ASCII).
pub fn replace_ignore_ascii_case(input: &str, pattern: &str, replacement: &str) -> String {
    debug_assert!(pattern.is_ascii() && !pattern.is_empty());
    let lower = input.to_ascii_lowercase();
    let pattern = pattern.to_ascii_lowercase();
    let mut out = String::with_capacity(input.len());
    let mut last = 0;
    // ASCII lowercasing keeps byte offsets identical, so indices map back onto `input`.
    for (start, _) in lower.match_indices(&pattern) {
        if start < last {
            continue;
        }
        out.push_str(&input[last..start]);
        out.push_str(replacement);
        last = start + pattern.len();
    }
    out.push_str(&input[last..]);
    out
}

/// First value that is not blank, trimmed; empty when all are blank.
pub fn first_non_empty(values: &[&str]) -> String {
    values
        .iter()
        .find(|value| !is_blank(value))
        .map(|value| value.trim().to_string())
        .unwrap_or_default()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn replace_is_case_insensitive_and_keeps_other_text() {
        assert_eq!(replace_ignore_ascii_case("NPC_dota_hero_axe", "npc_dota_hero_", ""), "axe");
        assert_eq!(replace_ignore_ascii_case("item_item_blink", "item_", ""), "blink");
        assert_eq!(replace_ignore_ascii_case("blink", "item_", ""), "blink");
    }

    #[test]
    fn first_non_empty_trims() {
        assert_eq!(first_non_empty(&["  ", " a "]), "a");
        assert_eq!(first_non_empty(&["", " "]), "");
    }
}
