using Mango.Services;

namespace Mango.Models;

public sealed class MatchPlayer
{
    public string SteamId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string TeamName { get; set; } = string.Empty;
    public int TeamSlot { get; set; }
    public string HeroName { get; set; } = string.Empty;
    public int HeroLevel { get; set; }
    public int Kills { get; set; }
    public int Deaths { get; set; }
    public int Assists { get; set; }
    public int LastHits { get; set; }
    public int Denies { get; set; }
    public int Gpm { get; set; }
    public int Xpm { get; set; }
    public int Gold { get; set; }

    public string Note { get; set; } = string.Empty;
    public int LikeCount { get; set; }
    public int DislikeCount { get; set; }
    public bool IsToxic { get; set; }
    public bool CanMarkToxic => !string.IsNullOrWhiteSpace(SteamId);
    public string ToxicToolTip => !CanMarkToxic ? "Player has no ID." : IsToxic ? "Remove toxic mark" : "Mark as toxic player";
    public bool CanVote { get; set; }
    public string VoteUnavailableReason { get; set; } = string.Empty;
    public string CurrentMatchVote { get; set; } = string.Empty;

    public string AvatarUrl { get; set; } = string.Empty;
    public string HeroImageUrl { get; set; } = string.Empty;
    public string RankIconUrl { get; set; } = string.Empty;
    public string RankStarUrl { get; set; } = string.Empty;
    public string RankLabel { get; set; } = string.Empty;
    /// <summary>True when the medal is inferred (match history or lobby average) rather than confirmed or set manually.</summary>
    public bool RankIsEstimated { get; set; }
    public int? ReportedRankTier { get; set; }
    public bool CanEditRank => PlayerRanks.CanOverride(IsProfilePrivate, ReportedRankTier)
        && SteamIdConverter.ToAccountId(SteamId) is not null;
    public string MatchesLabel { get; set; } = string.Empty;
    public bool IsProfilePrivate { get; set; }
    public bool IsLocalPlayer { get; set; }
    public PlayerMatchHistory History { get; set; } = new();

    public bool HasHeroLevel => HeroLevel > 0;
    public string HeroNameUpper => DisplayHeroName.ToUpperInvariant();
    public string GoldLabel => Gold > 0 ? Gold.ToString("N0") : "—";
    public string Kda => $"{Kills}/{Deaths}/{Assists}";
    public string LastHitsDenies => $"{LastHits}/{Denies}";
    public string DisplayHeroName => FormatHeroName(HeroName);
    public string HeroIconKey => FormatHeroIconKey(HeroName);
    public string NoteKey => SteamId;
    public string NoteButtonLabel => string.IsNullOrWhiteSpace(Note) ? "Add note" : "Edit note";
    public string NoteToolTip =>
        string.IsNullOrWhiteSpace(Note) ? NoteButtonLabel : $"{NoteButtonLabel}: {Note}";
    public string LikeToolTip =>
        !CanVote
            ? VoteUnavailableReason
            : string.Equals(CurrentMatchVote, "like", StringComparison.OrdinalIgnoreCase)
                ? "Remove like"
                : "Like";
    public string DislikeToolTip =>
        !CanVote
            ? VoteUnavailableReason
            : string.Equals(CurrentMatchVote, "dislike", StringComparison.OrdinalIgnoreCase)
                ? "Remove dislike"
                : "Dislike";

    public bool HasNote => !string.IsNullOrWhiteSpace(Note);
    public bool IsLikedThisMatch =>
        string.Equals(CurrentMatchVote, "like", StringComparison.OrdinalIgnoreCase);
    public bool IsDislikedThisMatch =>
        string.Equals(CurrentMatchVote, "dislike", StringComparison.OrdinalIgnoreCase);

    // Segoe Fluent Icons: outline Like/Dislike/Comment vs LikeSolid/DislikeSolid/CommentSolid.
    public string LikeGlyph => IsLikedThisMatch ? "\uF3BF" : "\uE8E1";
    public string DislikeGlyph => IsDislikedThisMatch ? "\uF3C0" : "\uE8E0";
    public string NoteGlyph => HasNote ? "\uEA3A" : "\uE932";

    private static string FormatHeroName(string heroName)
    {
        var key = FormatHeroIconKey(heroName);
        return string.IsNullOrWhiteSpace(key) ? string.Empty : key.Replace('_', ' ');
    }

    private static string FormatHeroIconKey(string heroName)
    {
        if (string.IsNullOrWhiteSpace(heroName))
        {
            return string.Empty;
        }

        const string prefix = "npc_dota_hero_";
        var key = heroName.Trim();
        if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            key = key[prefix.Length..];
        }

        return key.Replace(' ', '_').ToLowerInvariant();
    }
}
