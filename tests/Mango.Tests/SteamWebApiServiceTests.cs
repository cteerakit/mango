using System.Net;
using System.Text.Json;
using Mango.Services;
using Xunit;

namespace Mango.Tests;

public sealed class SteamWebApiServiceTests
{
    [Fact]
    public async Task GetRealtimeRosterAsync_RejectsDifferentKnownMatch()
    {
        using var client = new HttpClient(new ResponseHandler("""
            {"match":{"match_id":111},"teams":[{"team_number":2,"players":[{"accountid":42}]}]}
            """));
        var service = new SteamWebApiService(client);
        Assert.Null(await service.GetRealtimeRosterAsync("901", "test-key", expectedMatchId: "222"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetRealtimeRosterAsync_ReadsAllPlayersAndStats(bool wrapped)
    {
        var teams = new[] { 2, 3 }.Select(team => new
        {
            team_number = team,
            players = Enumerable.Range(0, 5).Select(slot => new
            {
                accountid = team * 10 + slot,
                name = $"Player {team}-{slot}",
                heroid = 14,
                team_slot = slot,
                level = 12,
                kill_count = 3,
                death_count = 2,
                assists_count = 7,
                lh_count = 84,
                denies_count = 6,
                gold = 1200,
            }),
        });
        var json = JsonSerializer.Serialize(new { match = new { match_id = "123" }, teams });
        if (wrapped)
        {
            json = "{\"result\":" + json + "}";
        }

        using var client = new HttpClient(new ResponseHandler(json));
        var service = new SteamWebApiService(client);
        var players = await service.GetRealtimeRosterAsync("90295161133586447", "test-key",
            new Dictionary<int, string> { [14] = "npc_dota_hero_pudge" }, expectedMatchId: "123");

        Assert.NotNull(players);
        Assert.Equal(10, players.Count);
        Assert.Equal("radiant", players[0].TeamName);
        Assert.Equal("dire", players[5].TeamName);
        Assert.Equal("20", players[0].SteamId);
        Assert.Equal("npc_dota_hero_pudge", players[0].HeroName);
        Assert.Equal("3/2/7", players[0].Kda);
        Assert.Equal("84/6", players[0].LastHitsDenies);
    }

    private sealed class ResponseHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
    }
}
