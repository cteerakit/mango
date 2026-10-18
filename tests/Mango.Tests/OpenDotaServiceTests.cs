using System.Net;
using Mango.Services;
using Xunit;

namespace Mango.Tests;

public sealed class OpenDotaServiceTests : IDisposable
{
    private readonly string _cacheDirectory = Path.Combine(Path.GetTempPath(), "Mango.Tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetPlayerOverviewAsync_IncludesTurboEvenWithOldFilteredCache(bool hasOldCache)
    {
        Directory.CreateDirectory(_cacheDirectory);
        if (hasOldCache)
        {
            await File.WriteAllTextAsync(Path.Combine(_cacheDirectory, "player_118993939_wl.json"), """{"win":1,"lose":4}""");
        }

        using var client = new HttpClient(new PlayerHandler())
        {
            BaseAddress = new Uri("https://api.opendota.com/api/"),
        };
        var service = new OpenDotaService(_cacheDirectory, client);

        var overview = await service.GetPlayerOverviewAsync("118993939");

        Assert.NotNull(overview);
        Assert.Equal(2187, overview.Wins + overview.Losses);
        Assert.Equal(1069, overview.Wins);
        Assert.Equal(1118, overview.Losses);
        Assert.Equal(48.9, overview.WinRatePercent);
    }

    [Fact]
    public async Task GetMatchRosterAsync_RejectsSameAccountInDifferentKnownMatch()
    {
        using var client = new HttpClient(new PlayerHandler("""
            [{"match_id":"111","players":[{"account_id":118993939,"hero_id":14}]}]
            """)) { BaseAddress = new Uri("https://api.opendota.com/api/") };
        var service = new OpenDotaService(_cacheDirectory, client);
        Assert.Null(await service.GetMatchRosterAsync("222", 118993939));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetPlayerOverviewAsync_PrivateReportedRankIsUnverified(bool isPrivate)
    {
        var profile = """{"rank_tier":63,"leaderboard_rank":123,"profile":{"fh_unavailable":PRIVATE}}"""
            .Replace("PRIVATE", isPrivate.ToString().ToLowerInvariant());
        using var client = new HttpClient(new PlayerHandler(profile: profile))
        {
            BaseAddress = new Uri("https://api.opendota.com/api/"),
        };
        var service = new OpenDotaService(_cacheDirectory, client);

        var overview = await service.GetPlayerOverviewAsync("118993939");

        Assert.NotNull(overview);
        Assert.Equal(isPrivate, overview.IsPrivate);
        Assert.Equal(63, overview.DisplayRankTier);
        Assert.Equal(isPrivate, overview.RankIsEstimated);
        if (isPrivate)
        {
            Assert.Contains("private profile", overview.RankLabel);
            Assert.DoesNotContain("#123", overview.RankLabel);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData(63)]
    public async Task GetPlayerOverviewAsync_EstimatesFromRecentRankedLobbiesAndRecord(int? oldTier)
    {
        Directory.CreateDirectory(_cacheDirectory);
        await File.WriteAllTextAsync(Path.Combine(_cacheDirectory, "player_118993939_ranked_recent.json"),
            """[{"match_id":10,"start_time":100,"lobby_type":7,"average_rank":11}]""");
        var profile = System.Text.Json.JsonSerializer.Serialize(new
        {
            rank_tier = oldTier,
            profile = new { fh_unavailable = true },
        });
        using var client = new HttpClient(new PlayerHandler(profile: profile, rankedMatches: """
            [{"match_id":10,"start_time":100,"lobby_type":7,"average_rank":44,"player_slot":0},
             {"match_id":20,"start_time":200,"lobby_type":7,"average_rank":44,"player_slot":128}]
            """)) { BaseAddress = new Uri("https://api.opendota.com/api/") };

        var overview = await new OpenDotaService(_cacheDirectory, client).GetPlayerOverviewAsync("118993939");

        Assert.NotNull(overview);
        Assert.Equal(44, overview.DisplayRankTier);
        Assert.True(overview.RankIsEstimated);
        Assert.Contains("teammates' ranks in 2 recent ranked matches", overview.RankLabel);
        Assert.Empty(overview.RecentMatches);
    }

    public void Dispose() => Directory.Delete(_cacheDirectory, recursive: true);

    private sealed class PlayerHandler(string? liveGames = null, string? profile = null, string? rankedMatches = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var json = uri.AbsolutePath switch
            {
                "/api/heroes" => "[]",
                "/api/live" => liveGames ?? "[]",
                "/api/players/118993939" => profile ?? """{"profile":{"personaname":"Turbo player"}}""",
                "/api/players/118993939/recentMatches" => "[]",
                "/api/players/118993939/matches" => RankedMatches(uri),
                // Same-side teammates are Legend 4, so the player's share is Legend 4 too.
                "/api/matches/10" or "/api/matches/20" => """
                    {"players":[{"player_slot":0},{"player_slot":1,"rank_tier":44},{"player_slot":2,"rank_tier":44},
                     {"player_slot":128},{"player_slot":129,"rank_tier":44},{"player_slot":130,"rank_tier":44},{"player_slot":131,"rank_tier":44},{"player_slot":132,"rank_tier":44}]}
                    """,
                "/api/players/118993939/wl" => uri.Query.Contains("significant=0", StringComparison.Ordinal)
                    ? """{"win":1069,"lose":1118}"""
                    : """{"win":1,"lose":4}""",
                _ => throw new InvalidOperationException($"Unexpected endpoint: {uri.AbsolutePath}"),
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }

        private string RankedMatches(Uri uri)
        {
            Assert.Contains("lobby_type=7", uri.Query);
            Assert.Contains("sort=start_time", uri.Query);
            Assert.Contains("limit=10&", uri.Query);
            return rankedMatches ?? "[]";
        }
    }
}
