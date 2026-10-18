using System.Text.Json;
using Mango.Models;
using Xunit;

namespace Mango.Tests;

public class SteamTopLiveMapperTests
{
    [Fact]
    public void FindGame_RejectsSameAccountInDifferentKnownMatch()
    {
        var payload = new SteamTopLiveResponse
        {
            GameList = [new SteamTopLiveGame
            {
                MatchId = "111",
                Players = [new SteamTopLivePlayer { AccountId = 42 }],
            }],
        };
        Assert.Null(SteamTopLiveMapper.FindGame(payload, 42, "222"));
    }

    [Fact]
    public void FindGame_MatchesLocalAccountAndMapsTenPlayers()
    {
        const string json =
            """
            {
              "game_list": [
                {
                  "server_steam_id": 90153324321222656,
                  "match_id": 8345123456,
                  "players": [
                    { "account_id": 1088417288, "hero_id": 14 },
                    { "account_id": 2, "hero_id": 1 },
                    { "account_id": 3, "hero_id": 2 },
                    { "account_id": 4, "hero_id": 3 },
                    { "account_id": 5, "hero_id": 4 },
                    { "account_id": 6, "hero_id": 5 },
                    { "account_id": 7, "hero_id": 6 },
                    { "account_id": 8, "hero_id": 7 },
                    { "account_id": 9, "hero_id": 8 },
                    { "account_id": 10, "hero_id": 9 }
                  ]
                }
              ]
            }
            """;

        var payload = JsonSerializer.Deserialize<SteamTopLiveResponse>(json, SteamTopLiveMapper.JsonOptions);
        var heroNames = new Dictionary<int, string> { [14] = "npc_dota_hero_pudge" };

        var hit = SteamTopLiveMapper.FindGame(payload, 1088417288, "8345123456", heroNames);

        Assert.NotNull(hit);
        Assert.Equal("90153324321222656", hit.ServerSteamId);
        Assert.Equal("8345123456", hit.MatchId);
        Assert.Equal(10, hit.Players.Count);
        Assert.All(hit.Players, static player => Assert.Equal(string.Empty, player.TeamName));
        Assert.Equal("npc_dota_hero_pudge", hit.Players[0].HeroName);
        Assert.Equal("1088417288", hit.Players[0].SteamId);
    }

    [Fact]
    public void FindGame_ReadsNestedResultWrapper()
    {
        const string json =
            """
            {
              "result": {
                "game_list": [
                  {
                    "server_steam_id": "9012",
                    "match_id": "1",
                    "players": [ { "account_id": 42, "hero_id": 1 } ]
                  }
                ]
              }
            }
            """;

        var payload = JsonSerializer.Deserialize<SteamTopLiveResponse>(json, SteamTopLiveMapper.JsonOptions);
        var hit = SteamTopLiveMapper.FindGame(payload, 42, null);

        Assert.NotNull(hit);
        Assert.Equal("9012", hit.ServerSteamId);
        Assert.Single(hit.Players);
    }

    [Fact]
    public void FindGame_ReturnsNullWhenAccountMissing()
    {
        const string json =
            """
            { "game_list": [ { "players": [ { "account_id": 1, "hero_id": 1 } ] } ] }
            """;

        var payload = JsonSerializer.Deserialize<SteamTopLiveResponse>(json, SteamTopLiveMapper.JsonOptions);
        Assert.Null(SteamTopLiveMapper.FindGame(payload, 99, null));
    }
}
