using Mango.Services;
using Xunit;

namespace Mango.Tests;

public sealed class RankEstimatorTests
{
    [Theory]
    [InlineData(0, 11)]
    [InlineData(153, 11)]
    [InlineData(154, 12)]
    [InlineData(770, 21)]
    [InlineData(3080, 51)]
    [InlineData(4619, 65)]
    [InlineData(4620, 71)]
    [InlineData(4820, 72)]
    [InlineData(5420, 75)]
    [InlineData(5619, 75)]
    [InlineData(5620, 80)]
    public void RankTierFromMmrUsesSeasonalBands(int mmr, int expectedTier)
    {
        Assert.Equal(expectedTier, RankEstimator.RankTierFromMmr(mmr));
    }

    [Fact]
    public void ApproximateMmrRoundTripsEveryTier()
    {
        foreach (var tier in PlayerRanks.Tiers)
        {
            Assert.Equal(tier, RankEstimator.RankTierFromMmr(RankEstimator.ApproximateMmrFromRankTier(tier)!.Value));
        }
    }

    private static (OpenDotaRecentMatch, IReadOnlyList<OpenDotaMatchPlayer>?) Match(int lobbyTier, params int?[] radiantTeammateTiers)
    {
        var players = new List<OpenDotaMatchPlayer> { new() { PlayerSlot = 0 } };
        players.AddRange(radiantTeammateTiers.Select((tier, i) => new OpenDotaMatchPlayer { PlayerSlot = i + 1, RankTier = tier }));
        players.AddRange(Enumerable.Range(128, 5).Select(slot => new OpenDotaMatchPlayer { PlayerSlot = slot, RankTier = 11 }));
        return (new OpenDotaRecentMatch { MatchId = 1, LobbyType = 7, PlayerSlot = 0, AverageRank = lobbyTier }, players);
    }

    private static double Mmr(int tier) => RankEstimator.ApproximateMmrFromRankTier(tier)!.Value;

    [Fact]
    public void FullyKnownTeamLeavesThePlayersShareOfTheLobbyTotal()
    {
        // Team totals 5 × Ancient 1; four Legend 1 teammates leave the player well above the lobby.
        var estimate = RankEstimator.EstimateFromTeammateRanks([Match(61, 51, 51, 51, 51)])!.Value;
        Assert.Equal(RankEstimator.RankTierFromMmr(5 * Mmr(61) - 4 * Mmr(51)), estimate.RankTier);
        Assert.True(estimate.RankTier > 61);
        Assert.Equal(1, estimate.MatchCount);
    }

    [Fact]
    public void UnrankedTeammatesSplitTheRemainderAndEnemiesAreIgnored()
    {
        var estimate = RankEstimator.EstimateFromTeammateRanks([Match(61, 71, 71, null, null)])!.Value;
        Assert.Equal((int)Math.Round((5 * Mmr(61) - 2 * Mmr(71)) / 3), estimate.Mmr);
    }

    [Fact]
    public void MedianIgnoresAnOutlierMatch()
    {
        var estimate = RankEstimator.EstimateFromTeammateRanks(
        [
            Match(52, 52, 52, 52, 52),
            Match(52, 52, 52, 52, 52),
            Match(52, 11, 11, 11, 11),
        ])!.Value;
        Assert.Equal(52, estimate.RankTier);
        Assert.Equal(3, estimate.MatchCount);
    }

    [Fact]
    public void MatchesWithoutKnownTeammatesOrLobbyRankAreSkipped()
    {
        var (match, players) = Match(52, 52, 52, 52, 52);
        Assert.Null(RankEstimator.EstimateFromTeammateRanks([Match(52, null, null, null, null)]));
        Assert.Null(RankEstimator.EstimateFromTeammateRanks([(match, null)]));
        match.AverageRank = null;
        Assert.Null(RankEstimator.EstimateFromTeammateRanks([(match, players)]));
        Assert.Null(RankEstimator.EstimateFromTeammateRanks([]));
    }

    [Fact]
    public void ResolvePrefersConfirmedTierThenHistoryThenLobbyAverage()
    {
        var history = new RankEstimate(52, 3200, 8);

        Assert.False(RankEstimator.Resolve(63, null, history, null, 41).IsEstimated);
        Assert.Equal(63, RankEstimator.Resolve(63, null, history, null, 41).RankTier);

        var fromHistory = RankEstimator.Resolve(null, null, history, 5000, 41);
        Assert.Equal(52, fromHistory.RankTier);
        Assert.True(fromHistory.IsEstimated);
        Assert.Equal("Estimated: Legend 2 (~3,200 MMR from teammates' ranks in 8 recent ranked matches)", fromHistory.Label);

        Assert.Equal(72, RankEstimator.Resolve(null, null, null, 4900, 41).RankTier);
        Assert.Equal(41, RankEstimator.Resolve(null, null, null, null, 41).RankTier);
    }
}
