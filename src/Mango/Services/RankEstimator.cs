namespace Mango.Services;

/// <summary>
/// Resolves a display medal from confirmed <c>rank_tier</c>, teammate ranks in recent ranked matches,
/// a legacy OpenDota MMR estimate, or the current lobby average.
/// MMR bands follow the in-game seasonal table: 154 MMR per star through Ancient,
/// 200 per star in Divine, Immortal from 5620.
/// </summary>
public static class RankEstimator
{
    private const int StarWidthMmr = 154;
    private const int DivineFloorMmr = 4620;
    private const int DivineStarWidthMmr = 200;
    private const int ImmortalFloorMmr = 5620;
    private const int RankedLobbyType = 7;
    private const int TeamSize = 5;
    private const double MaxEstimatedMmr = 8000;

    public static ResolvedRank Resolve(
        int? rankTier,
        int? leaderboardRank,
        RankEstimate? historyEstimate,
        int? legacyMmrEstimate,
        int? lobbyAverageTiers,
        bool isProfilePrivate = false)
    {
        if (rankTier is > 0 && (!isProfilePrivate || historyEstimate is null))
        {
            return new ResolvedRank(
                rankTier.Value,
                isProfilePrivate
                    ? $"Estimated: {OpenDotaService.FormatRankTier(rankTier)} (last reported rank; private profile, current rank unverified)"
                    : OpenDotaService.FormatRankTier(rankTier, leaderboardRank),
                IsEstimated: isProfilePrivate);
        }

        if (historyEstimate is { } history)
        {
            var name = OpenDotaService.FormatRankTier(history.RankTier);
            return new ResolvedRank(
                history.RankTier,
                $"Estimated: {name} (~{history.Mmr:N0} MMR from teammates' ranks in {history.MatchCount} recent ranked {(history.MatchCount == 1 ? "match" : "matches")})",
                IsEstimated: true);
        }

        if (legacyMmrEstimate is > 0)
        {
            var fromMmr = RankTierFromMmr(legacyMmrEstimate.Value);
            if (fromMmr is > 0)
            {
                var name = OpenDotaService.FormatRankTier(fromMmr);
                return new ResolvedRank(
                    fromMmr.Value,
                    $"Estimated: {name} (~{legacyMmrEstimate.Value:N0} MMR)",
                    IsEstimated: true);
            }
        }

        if (lobbyAverageTiers is > 0)
        {
            var name = OpenDotaService.FormatRankTier(lobbyAverageTiers);
            return new ResolvedRank(
                lobbyAverageTiers.Value,
                $"Lobby avg: ~{name}",
                IsEstimated: true);
        }

        return new ResolvedRank(null, string.Empty, IsEstimated: false);
    }

    /// <summary>
    /// Estimates the player's MMR from their recent ranked matches, assuming both teams in each
    /// match average the lobby rank Valve reports: the player's team totals five times the lobby
    /// MMR, so subtracting the known teammates leaves the player's share. Unranked teammates split
    /// that remainder evenly with the player. Matches are combined with a median weighted by how
    /// many teammates were known, so one teammate with a stale rank cannot skew the result.
    /// </summary>
    public static RankEstimate? EstimateFromTeammateRanks(
        IEnumerable<(OpenDotaRecentMatch Match, IReadOnlyList<OpenDotaMatchPlayer>? Players)> matches)
    {
        var samples = new List<(double Mmr, int Weight)>();
        foreach (var (match, players) in matches)
        {
            if (match.LobbyType != RankedLobbyType || players is null
                || match.AverageRank is not { } lobbyTier || !PlayerRanks.Tiers.Contains(lobbyTier))
            {
                continue;
            }

            var isRadiant = match.PlayerSlot < 128;
            var knownTeammateMmrs = players
                .Where(player => player.PlayerSlot != match.PlayerSlot && (player.PlayerSlot < 128) == isRadiant)
                .Select(static player => player.RankTier is { } tier && PlayerRanks.Tiers.Contains(tier)
                    ? ApproximateMmrFromRankTier(tier)
                    : null)
                .Where(static mmr => mmr.HasValue)
                .Select(static mmr => mmr!.Value)
                .Take(TeamSize - 1)
                .ToList();
            if (knownTeammateMmrs.Count == 0)
            {
                continue;
            }

            var teamMmr = TeamSize * ApproximateMmrFromRankTier(lobbyTier)!.Value;
            var share = (teamMmr - knownTeammateMmrs.Sum()) / (TeamSize - knownTeammateMmrs.Count);
            samples.Add((Math.Clamp(share, 0, MaxEstimatedMmr), knownTeammateMmrs.Count));
        }

        if (samples.Count == 0)
        {
            return null;
        }

        var mmr = WeightedMedian(samples);
        return new RankEstimate(RankTierFromMmr(mmr)!.Value, (int)Math.Round(mmr), samples.Count);
    }

    private static double WeightedMedian(List<(double Value, int Weight)> samples)
    {
        var ordered = samples.OrderBy(static sample => sample.Value).ToList();
        var half = ordered.Sum(static sample => sample.Weight) / 2.0;
        var cumulative = 0.0;
        for (var i = 0; i < ordered.Count; i++)
        {
            cumulative += ordered[i].Weight;
            if (cumulative > half)
            {
                return ordered[i].Value;
            }

            if (cumulative == half)
            {
                return (ordered[i].Value + ordered[i + 1].Value) / 2;
            }
        }

        return ordered[^1].Value;
    }

    public static int? AverageRankTier(IEnumerable<int> confirmedTiers)
    {
        var mmrs = confirmedTiers
            .Where(static tier => tier > 0)
            .Select(ApproximateMmrFromRankTier)
            .Where(static mmr => mmr.HasValue)
            .Select(static mmr => mmr!.Value)
            .ToList();

        if (mmrs.Count == 0)
        {
            return null;
        }

        return RankTierFromMmr(mmrs.Average());
    }

    public static int? RankTierFromMmr(double mmr)
    {
        if (mmr < 0)
        {
            return null;
        }

        if (mmr >= ImmortalFloorMmr)
        {
            return 80;
        }

        if (mmr >= DivineFloorMmr)
        {
            var divineStars = Math.Clamp((int)((mmr - DivineFloorMmr) / DivineStarWidthMmr) + 1, 1, 5);
            return 70 + divineStars;
        }

        var step = (int)(mmr / StarWidthMmr);
        return (step / 5 + 1) * 10 + step % 5 + 1;
    }

    public static double? ApproximateMmrFromRankTier(int rankTier)
    {
        var medal = rankTier / 10;
        var stars = rankTier % 10;
        if (medal <= 0)
        {
            return null;
        }

        if (medal >= 8)
        {
            return ImmortalFloorMmr + 300;
        }

        stars = Math.Clamp(stars <= 0 ? 1 : stars, 1, 5);
        if (medal == 7)
        {
            return DivineFloorMmr + (stars - 1) * DivineStarWidthMmr + DivineStarWidthMmr / 2.0;
        }

        return ((medal - 1) * 5 + stars - 1) * StarWidthMmr + StarWidthMmr / 2.0;
    }
}

public readonly record struct ResolvedRank(int? RankTier, string Label, bool IsEstimated);

public readonly record struct RankEstimate(int RankTier, int Mmr, int MatchCount);
