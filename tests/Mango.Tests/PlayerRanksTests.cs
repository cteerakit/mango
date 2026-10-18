using System.Text.Json;
using Mango.Models;
using Mango.Services;
using Xunit;

namespace Mango.Tests;

public sealed class PlayerRanksTests
{
    [Theory]
    [InlineData(true, 54, true)]
    [InlineData(true, null, true)]
    [InlineData(false, 54, false)]
    [InlineData(false, null, true)]
    [InlineData(false, 0, true)]
    public void PrivateOrMissingReportedRankCanEditAndUseSavedRanks(bool isPrivate, int? reportedRankTier, bool expected)
    {
        var player = new MatchPlayer { SteamId = "12345", IsProfilePrivate = isPrivate, ReportedRankTier = reportedRankTier };
        var ranks = new Dictionary<string, PlayerRankOverride>
        {
            ["12345"] = new() { RankTier = 54 },
        };

        Assert.Equal(expected, player.CanEditRank);
        Assert.Equal(expected, PlayerRanks.Get(ranks, player.SteamId, isPrivate, reportedRankTier) is not null);

        player.SteamId = string.Empty;
        Assert.False(player.CanEditRank);
    }

    [Fact]
    public void PublicProfileStopsUsingSavedRankWhenReportedRankAppears()
    {
        var ranks = new Dictionary<string, PlayerRankOverride>
        {
            ["12345"] = new() { RankTier = 54 },
        };

        Assert.Equal(54, PlayerRanks.Get(ranks, "12345", isProfilePrivate: true)?.RankTier);
        Assert.Equal(54, PlayerRanks.Get(ranks, "12345", isProfilePrivate: false)?.RankTier);
        Assert.Null(PlayerRanks.Get(ranks, "12345", isProfilePrivate: false, reportedRankTier: 63));
    }

    [Fact]
    public void SavedRankSurvivesSerializationAndMatchesEitherPlayerIdFormat()
    {
        var ranks = new Dictionary<string, PlayerRankOverride>
        {
            ["12345"] = new() { PlayerId = "12345", PlayerName = "Private player", RankTier = 54 },
        };

        var restored = PlayerRanks.Normalize(JsonSerializer.Deserialize<Dictionary<string, PlayerRankOverride>>(
            JsonSerializer.Serialize(ranks)));

        Assert.Equal(54, PlayerRanks.Get(restored, "76561197960278073", isProfilePrivate: true)?.RankTier);
        Assert.Equal("Private player", PlayerRanks.Get(restored, "12345", isProfilePrivate: true)?.PlayerName);
        Assert.Null(PlayerRanks.Get(restored, "67890", isProfilePrivate: true));

        restored.Remove("12345");
        var cleared = PlayerRanks.Normalize(JsonSerializer.Deserialize<Dictionary<string, PlayerRankOverride>>(
            JsonSerializer.Serialize(restored)));
        Assert.Null(PlayerRanks.Get(cleared, "76561197960278073", isProfilePrivate: true));
    }

    [Fact]
    public void NormalizeConvertsSteamIdsAndRejectsInvalidRanksAndPlayers()
    {
        var ranks = PlayerRanks.Normalize(new()
        {
            ["76561197960278073"] = new() { RankTier = 80 },
            ["23456"] = new() { RankTier = 76 },
            ["34567"] = new() { RankTier = 0 },
            ["unknown"] = new() { RankTier = 35 },
        });

        Assert.Single(ranks);
        Assert.Equal(80, ranks["12345"].RankTier);
        Assert.Null(PlayerRanks.Get(ranks, null, isProfilePrivate: true));
        Assert.Empty(PlayerRanks.Normalize(null));
    }
}
