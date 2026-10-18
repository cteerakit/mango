namespace Mango.Models;

public sealed class PlayerMatchHistory
{
    public string Kda { get; init; } = "—";
    public string Result { get; init; } = string.Empty;
    public int? Position { get; init; }
    public string PositionBasis { get; init; } = "OpenDota parsed-match estimate.";
    /// <summary>False when the position comes from a replay-analysed source such as STRATZ.</summary>
    public bool IsPositionEstimated { get; init; } = true;
    public string PositionLabel => FormatPositionLabel(Position, IsPositionEstimated);
    public string PositionToolTip => Position is >= 1 and <= 5
        ? $"{PositionLabel}\n{PositionBasis}" : PositionLabel;
    public static string FormatPositionLabel(int? position, bool estimated = true)
    {
        var role = position switch
        {
            1 => "Pos 1 · Carry",
            2 => "Pos 2 · Mid",
            3 => "Pos 3 · Offlane",
            4 => "Pos 4 · Soft support",
            5 => "Pos 5 · Hard support",
            _ => null,
        };
        return role is null ? "Position unavailable" : estimated ? $"{role} (estimated)" : role;
    }
    public DateTimeOffset? PlayedAt { get; init; }
    public string ResultKda => string.IsNullOrEmpty(Result) ? Kda : $"{Result} {Kda}";
    public string RelativeMatchTime => PlayedAt is { } playedAt
        ? FormatRelativeTime(playedAt, DateTimeOffset.UtcNow) : string.Empty;

    public static string FormatRelativeTime(DateTimeOffset playedAt, DateTimeOffset now)
    {
        var elapsed = now - playedAt;
        if (elapsed.TotalMinutes < 1) return "Just now";
        if (elapsed.TotalHours < 1) return Ago((int)elapsed.TotalMinutes, "minute");
        if (elapsed.TotalDays < 1) return Ago((int)elapsed.TotalHours, "hour");
        if (elapsed.TotalDays < 30) return Ago((int)elapsed.TotalDays, "day");
        if (elapsed.TotalDays < 365) return Ago((int)(elapsed.TotalDays / 30), "month");
        return Ago((int)(elapsed.TotalDays / 365), "year");
    }

    private static string Ago(int count, string unit) => $"{count} {unit}{(count == 1 ? "" : "s")} ago";

    public string EncounterLabel { get; init; } = "Loading past encounters…";
    public string HeroMatchLabel { get; init; } = "Loading latest hero match…";
    public string HeroMatchUrl { get; init; } = string.Empty;
    public bool HasHeroMatch => !string.IsNullOrEmpty(HeroMatchUrl);
    public IReadOnlyList<MatchBuildItem> Items { get; init; } = [];
    public IReadOnlyList<MatchBuildItem> VisibleItems => Items.Take(11).ToList();
    public IReadOnlyList<MatchBuildItem> OverflowItems => Items.Skip(11).ToList();
    public bool HasOverflowItems => Items.Count > 11;
    public string OverflowLabel => $"+{Math.Max(0, Items.Count - 11)}";
    public IReadOnlyList<RecentMatchSummary> RecentMatches { get; init; } = [];
    public string RecentMatchesLabel { get; init; } = "Loading recent matches…";
    public bool HasRecentMatches => RecentMatches.Count > 0;
}

public sealed class RecentMatchSummary
{
    public string HeroName { get; init; } = string.Empty;
    public string HeroImageUrl { get; init; } = string.Empty;
    public string Result { get; init; } = string.Empty;
    public string ToolTip => $"{HeroName} · {Result}";
}

public sealed class MatchBuildItem
{
    public string Name { get; init; } = string.Empty;
    public string ImageUrl { get; init; } = string.Empty;
    public string Slot { get; init; } = string.Empty;
    public int? PurchaseTimeSeconds { get; init; }
    public string PurchaseTimeLabel => PurchaseTimeSeconds is { } seconds
        ? $"{(seconds < 0 ? "−" : "")}{Math.Abs((long)seconds) / 60:00}:{Math.Abs((long)seconds) % 60:00}" : "—";
    public string ToolTip => $"{Name} · {Slot} · {PurchaseTimeLabel}";
}
