//! Image URLs for heroes, items and medals (C# `AssetUrls`).

const HERO_CDN: &str = "https://cdn.cloudflare.steamstatic.com/apps/dota2/images/dota_react/heroes";
const ITEM_CDN: &str = "https://cdn.cloudflare.steamstatic.com/apps/dota2/images/dota_react/items";
const RANK_ICON_CDN: &str = "https://www.opendota.com/assets/images/dota2/rank_icons";

/// `key` is the hero name without `npc_dota_hero_`.
pub fn hero_icon(key: &str) -> String {
    if key.trim().is_empty() {
        String::new()
    } else {
        format!("{HERO_CDN}/{key}.png")
    }
}

/// `key` is the item name without `item_`.
pub fn item_icon(key: &str) -> String {
    if key.trim().is_empty() {
        String::new()
    } else {
        format!("{ITEM_CDN}/{key}.png")
    }
}

/// OpenDota medal icon for a `rank_tier` (e.g. 55 = Legend 5).
pub fn rank_icon(rank_tier: Option<i32>) -> String {
    let medal = rank_tier.filter(|t| *t > 0).map_or(0, |t| (t / 10).clamp(0, 8));
    format!("{RANK_ICON_CDN}/rank_icon_{medal}.png")
}

/// Star overlay for Herald–Divine; empty for unranked and Immortal.
pub fn rank_star(rank_tier: Option<i32>) -> String {
    let Some(tier) = rank_tier.filter(|t| *t > 0) else { return String::new() };
    let (medal, stars) = (tier / 10, (tier % 10).clamp(0, 5));
    if !(1..=7).contains(&medal) || stars < 1 {
        return String::new();
    }
    format!("{RANK_ICON_CDN}/rank_star_{stars}.png")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn medal_urls() {
        assert!(rank_icon(Some(55)).ends_with("rank_icon_5.png"));
        assert!(rank_icon(None).ends_with("rank_icon_0.png"));
        assert!(rank_star(Some(55)).ends_with("rank_star_5.png"));
        assert_eq!(rank_star(Some(80)), "");
        assert_eq!(rank_star(Some(50)), "");
        assert!(hero_icon("axe").ends_with("/heroes/axe.png"));
        assert_eq!(item_icon(""), "");
    }
}
