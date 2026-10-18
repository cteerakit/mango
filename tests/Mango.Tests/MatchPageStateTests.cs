using Mango.Models;
using Xunit;

namespace Mango.Tests;

public sealed class MatchPageStateTests
{
    [Fact]
    public void FinishedMatch_RemainsVisibleThroughPostGameMenuAndDisconnect()
    {
        var state = new MatchPageState();
        var match = Match("100");
        state.Update(match);
        state.Update(new MatchSnapshot { IsConnected = true, MatchId = "100", GameState = "DOTA_GAMERULES_STATE_POST_GAME" });
        state.Update(new MatchSnapshot { IsConnected = true, GameState = "DOTA_GAMERULES_STATE_DISCONNECT" });
        state.Update(new MatchSnapshot { IsConnected = false });

        Assert.Same(match, state.Snapshot);
        Assert.Equal("100", state.Snapshot.MatchId);
        Assert.Equal(10, state.Snapshot.Players.Count);
        Assert.True(state.IsPreviousMatch);
    }

    [Theory]
    [InlineData("DOTA_GAMERULES_STATE_HERO_SELECTION", true)]
    [InlineData("DOTA_GAMERULES_STATE_STRATEGY_TIME", true)]
    [InlineData("DOTA_GAMERULES_STATE_WAIT_FOR_PLAYERS_TO_LOAD", true)]
    [InlineData("DOTA_GAMERULES_STATE_TEAM_SHOWCASE", true)]
    [InlineData("DOTA_GAMERULES_STATE_PRE_GAME", true)]
    [InlineData("DOTA_GAMERULES_STATE_GAME_IN_PROGRESS", true)]
    [InlineData("DOTA_GAMERULES_STATE_POST_GAME", false)]
    [InlineData("", false)]
    public void IsInMatch_CoversDraftThroughGame(string gameState, bool expected) =>
        Assert.Equal(expected, new MatchSnapshot { GameState = gameState }.IsInMatch);

    [Theory]
    [InlineData("", "DOTA_GAMERULES_STATE_HERO_SELECTION")]
    [InlineData("200", "DOTA_GAMERULES_STATE_WAIT_FOR_PLAYERS_TO_LOAD")]
    [InlineData("200", "DOTA_GAMERULES_STATE_PRE_GAME")]
    [InlineData("200", "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS")]
    public void NewMatch_ReplacesPreviousRoster(string matchId, string phase)
    {
        var state = new MatchPageState();
        state.Update(Match("100"));
        state.Update(new MatchSnapshot { IsConnected = true });
        var next = new MatchSnapshot { IsConnected = true, MatchId = matchId, GameState = phase };
        state.Update(next);

        Assert.Same(next, state.Snapshot);
        Assert.Empty(state.Snapshot.Players);
        Assert.False(state.IsPreviousMatch);
    }

    [Fact]
    public void SparsePostGame_RetainsPlayersUntilFinalRosterArrives()
    {
        var state = new MatchPageState();
        state.Update(Match("100"));
        state.Update(new MatchSnapshot { IsConnected = true, MatchId = "100", GameState = "DOTA_GAMERULES_STATE_POST_GAME" });
        Assert.Equal(10, state.Snapshot.Players.Count);
        Assert.True(state.IsPreviousMatch);

        var final = Match("100");
        final.GameState = "DOTA_GAMERULES_STATE_POST_GAME";
        state.Update(final);
        Assert.Same(final, state.Snapshot);
    }

    private static MatchSnapshot Match(string id) => new()
    {
        IsConnected = true,
        MatchId = id,
        GameState = "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS",
        Players = Enumerable.Range(1, 10).Select(i => new MatchPlayer { SteamId = i.ToString(), Name = $"Player {i}" }).ToList(),
    };
}
