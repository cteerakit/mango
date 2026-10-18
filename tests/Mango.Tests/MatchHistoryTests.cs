using System.Net;
using Mango.Services;
using Xunit;

namespace Mango.Tests;

public sealed class MatchHistoryTests : IDisposable
{
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "Mango.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PurchaseTimelineIncludesSoldItemsRepeatedPurchasesAndOverflowInTimeOrder()
    {
        var log = string.Join(",", Enumerable.Range(0, 20).Reverse()
            .Select(index => $$"""{"key":"blink","time":{{175 + index * 60}}}"""));
        using var client = Client(request => request.AbsolutePath switch
        {
            "/api/heroes" => """[{"id":14,"name":"npc_dota_hero_pudge","localized_name":"Pudge"}]""",
            "/api/players/456/matches" => CheckHeroFilter(request),
            "/api/matches/90" => $$"""{"players":[{"account_id":456,"hero_id":14,"item_0":1,"purchase_log":[{{log}}]}]}""",
            "/api/constants/items" => """{"blink":{"id":1,"dname":"Blink Dagger"},"boots":{"id":2,"dname":"Boots of Speed"}}""",
            _ => throw new InvalidOperationException(request.ToString()),
        });
        var build = await new OpenDotaService(_cache, client).GetLatestHeroBuildAsync("456", "pudge", "100");
        Assert.Equal(20, build.Items.Count);
        Assert.All(build.Items, item => Assert.Equal("Blink Dagger", item.Name));
        Assert.Equal("02:55", build.VisibleItems[0].PurchaseTimeLabel);
        Assert.Equal(11, build.VisibleItems.Count);
        Assert.Equal("13:55", build.OverflowItems[0].PurchaseTimeLabel);
        Assert.Equal(9, build.OverflowItems.Count);
        Assert.True(build.HasOverflowItems);
        Assert.Equal("+9", build.OverflowLabel);
        Assert.Contains("Purchase timeline", build.HeroMatchLabel);
    }

    [Theory]
    [InlineData(null, "—")]
    [InlineData(-65, "−01:05")]
    [InlineData(0, "00:00")]
    [InlineData(3601, "60:01")]
    public void PurchaseTimeUsesMatchMinutesAndSeconds(int? seconds, string label)
    {
        Assert.Equal(label, new Mango.Models.MatchBuildItem { PurchaseTimeSeconds = seconds }.PurchaseTimeLabel);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BuildShowsOnlyKeyItemsForPurchasesAndFinalInventory(bool hasPurchaseLog)
    {
        var log = hasPurchaseLog ? """
            ,"purchase_log":[{"key":"tango","time":0},{"key":"boots","time":175},
            {"key":"mithril_hammer","time":300},{"key":"recipe_black_king_bar","time":400},
            {"key":"black_king_bar","time":500},{"key":"magic_wand","time":600},
            {"key":"force_staff","time":700},{"key":"trusty_shovel","time":800}]
            """ : "";
        using var client = Client(request => request.AbsolutePath switch
        {
            "/api/heroes" => """[{"id":14,"name":"npc_dota_hero_pudge","localized_name":"Pudge"}]""",
            "/api/players/456/matches" => CheckHeroFilter(request),
            "/api/matches/90" => $$"""
                {"players":[{"account_id":456,"hero_id":14,"item_0":1,"item_1":2,"item_2":3,
                "item_3":4,"item_4":5,"item_5":6,"backpack_0":7,"item_neutral":8{{log}}}]}
                """,
            "/api/constants/items" => """
                {"tango":{"id":1,"dname":"Tango","cost":90},
                "boots":{"id":2,"dname":"Boots of Speed","cost":500},
                "mithril_hammer":{"id":3,"dname":"Mithril Hammer","cost":1600},
                "recipe_black_king_bar":{"id":4,"dname":"BKB Recipe","cost":1450},
                "black_king_bar":{"id":5,"dname":"Black King Bar","cost":4050,"components":["mithril_hammer","ogre_axe"]},
                "magic_wand":{"id":6,"dname":"Magic Wand","cost":450,"components":["magic_stick","branches"]},
                "force_staff":{"id":7,"dname":"Force Staff","cost":2200,"components":["staff_of_wizardry","fluffy_hat"]},
                "trusty_shovel":{"id":8,"dname":"Trusty Shovel","cost":0}}
                """,
            _ => throw new InvalidOperationException(request.ToString()),
        });
        var build = await new OpenDotaService(_cache, client).GetLatestHeroBuildAsync("456", "pudge", "100");
        Assert.Equal(new[] { "Black King Bar", "Force Staff" }, build.Items.Select(item => item.Name));
        Assert.False(build.HasOverflowItems);
        if (hasPurchaseLog)
            Assert.Equal(new int?[] { 500, 700 }, build.Items.Select(item => item.PurchaseTimeSeconds));
    }

    [Fact]
    public async Task RecentMatchesShowFiveNewestHeroesAndPlayerResultsExcludingDisplayedMatch()
    {
        using var client = Client(request => request.AbsolutePath switch
        {
            "/api/heroes" => """[{"id":14,"name":"npc_dota_hero_pudge","localized_name":"Pudge"}]""",
            "/api/players/456/recentMatches" => """
                [{"match_id":1,"hero_id":14,"start_time":1,"player_slot":0,"radiant_win":true},
                 {"match_id":7,"hero_id":14,"start_time":7,"player_slot":0,"radiant_win":true},
                 {"match_id":6,"hero_id":14,"start_time":6,"player_slot":128,"radiant_win":false},
                 {"match_id":5,"hero_id":14,"start_time":5,"player_slot":128,"radiant_win":true},
                 {"match_id":4,"hero_id":14,"start_time":4,"player_slot":0,"radiant_win":false},
                 {"match_id":3,"hero_id":14,"start_time":3,"player_slot":0,"radiant_win":true},
                 {"match_id":2,"hero_id":14,"start_time":2,"player_slot":128,"radiant_win":false}]
                """,
            _ => throw new InvalidOperationException(request.ToString()),
        });
        var (matches, _) = await new OpenDotaService(_cache, client)
            .GetRecentMatchSummariesAsync("76561197960266184", "7");
        Assert.Equal(new[] { "Win", "Loss", "Loss", "Win", "Win" }, matches.Select(match => match.Result));
        Assert.All(matches, match =>
        {
            Assert.Equal("Pudge", match.HeroName);
            Assert.EndsWith("/heroes/pudge.png", match.HeroImageUrl);
            Assert.Equal($"Pudge · {match.Result}", match.ToolTip);
        });
    }

    [Theory]
    [InlineData("[]", "No recent matches recorded by OpenDota.")]
    [InlineData(null, "Recent matches unavailable.")]
    public async Task RecentMatchesDistinguishEmptyAndUnavailableHistory(string? json, string expected)
    {
        using var client = Client(request => request.AbsolutePath == "/api/heroes" ? "[]" : json);
        var (matches, label) = await new OpenDotaService(_cache, client)
            .GetRecentMatchSummariesAsync("456", "100");
        Assert.Empty(matches);
        Assert.Equal(expected, label);
    }

    [Fact]
    public async Task EncountersNormalizeSteamIdsIncludeTurboAndExcludeDisplayedMatch()
    {
        using var client = Client(request =>
        {
            Assert.Equal("/api/players/123/matches", request.AbsolutePath);
            Assert.Contains("significant=0", request.Query);
            return request.Query.Contains("with_account_id=456")
                ? """[{"match_id":90},{"match_id":100}]"""
                : """[{"match_id":80}]""";
        });
        var label = await new OpenDotaService(_cache, client)
            .GetEncounterLabelAsync("76561197960265851", "456", "100");
        Assert.Equal("1/1", label);
    }

    [Fact]
    public async Task EmptyRecordedHistoryIsDifferentFromUnavailableHistory()
    {
        using var empty = Client(_ => "[]");
        var service = new OpenDotaService(_cache, empty);
        Assert.Equal("0/0",
            await service.GetEncounterLabelAsync("123", "456", "100"));
        using var failed = Client(_ => null);
        Assert.Equal("Encounter history unavailable.",
            await new OpenDotaService(_cache, failed).GetEncounterLabelAsync("123", "789", "100"));
    }

    [Fact]
    public async Task LatestBuildUsesKeyInventoryAndBackpackItemsForCorrectPlayer()
    {
        using var client = Client(request => request.AbsolutePath switch
        {
            "/api/heroes" => """[{"id":14,"name":"npc_dota_hero_pudge","localized_name":"Pudge"}]""",
            "/api/players/456/matches" => CheckHeroFilter(request),
            "/api/matches/90" => """
                {"players":[
                  {"account_id":777,"hero_id":14,"item_0":999},
                  {"account_id":456,"hero_id":14,"item_0":1,"item_1":1,"item_2":999,"item_3":0,
                   "backpack_0":1,"item_neutral":3}]}
                """,
            "/api/constants/items" => """
                {"blink":{"id":1,"dname":"Blink Dagger"},"boots":{"id":2,"dname":"Boots of Speed"},
                 "trusty_shovel":{"id":3,"dname":"Trusty Shovel"}}
                """,
            _ => throw new InvalidOperationException(request.ToString()),
        });
        var build = await new OpenDotaService(_cache, client)
            .GetLatestHeroBuildAsync("456", "npc_dota_hero_pudge", "100");
        Assert.Equal("https://www.opendota.com/matches/90", build.HeroMatchUrl);
        Assert.Equal("3/4/5", build.Kda);
        Assert.Equal("Loss 3/4/5", build.ResultKda);
        Assert.Null(build.Position);
        Assert.Equal("Position unavailable", build.PositionLabel);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), build.PlayedAt);
        Assert.Contains("Latest Pudge · Loss · 3/4/5", build.HeroMatchLabel);
        Assert.Equal(new[] { "Blink Dagger", "Blink Dagger", "Blink Dagger" },
            build.Items.Select(item => item.Name));
        Assert.EndsWith("/blink.png", build.Items[0].ImageUrl);
        Assert.Equal("Backpack", build.Items[2].Slot);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("""[{"key":"blink","time":-30},{"key":"blink","time":1200},{"key":"blink","time":600}]""")]
    public async Task BuildUsesPurchaseTimelineWhenAvailableAndFinalInventoryOtherwise(string purchaseLog)
    {
        using var client = Client(request => request.AbsolutePath switch
        {
            "/api/heroes" => """[{"id":14,"name":"npc_dota_hero_pudge","localized_name":"Pudge"}]""",
            "/api/players/456/matches" => CheckHeroFilter(request),
            "/api/matches/90" => """
                {"players":[{"account_id":456,"hero_id":14,"item_0":1,"item_1":999,
                "item_2":1,"backpack_0":2,"item_neutral":3,"purchase_log":PURCHASE_LOG}]}
                """.Replace("PURCHASE_LOG", purchaseLog),
            "/api/constants/items" => """
                {"blink":{"id":1,"dname":"Blink Dagger"},"boots":{"id":2,"dname":"Boots of Speed"},
                 "trusty_shovel":{"id":3,"dname":"Trusty Shovel"}}
                """,
            _ => throw new InvalidOperationException(request.ToString()),
        });
        var build = await new OpenDotaService(_cache, client).GetLatestHeroBuildAsync("456", "pudge", "100");
        if (purchaseLog.StartsWith('[') && purchaseLog.Length > 2)
        {
            Assert.Equal(new[] { "Blink Dagger", "Blink Dagger", "Blink Dagger" },
                build.Items.Select(item => item.Name));
            Assert.Equal(new int?[] { -30, 600, 1200 }, build.Items.Select(item => item.PurchaseTimeSeconds));
            Assert.Equal("Purchase", build.Items[0].Slot);
        }
        else
        {
            Assert.Equal(new[] { "Blink Dagger", "Blink Dagger" },
                build.Items.Select(item => item.Name));
            Assert.All(build.Items, item => Assert.Null(item.PurchaseTimeSeconds));
        }
    }

    [Fact]
    public async Task BuildUsesStratzPurchaseTimesWhenOpenDotaHasNoPurchaseLog()
    {
        using var client = Client(request => request.AbsolutePath switch
        {
            "/api/heroes" => """[{"id":14,"name":"npc_dota_hero_pudge","localized_name":"Pudge"}]""",
            "/api/players/456/matches" => CheckHeroFilter(request),
            "/api/matches/90" => """{"players":[{"account_id":456,"hero_id":14,"item_0":5}]}""",
            "/api/constants/items" => """
                {"mithril_hammer":{"id":3,"dname":"Mithril Hammer","cost":1600},
                "black_king_bar":{"id":5,"dname":"Black King Bar","cost":4050,"components":["mithril_hammer","ogre_axe"]},
                "force_staff":{"id":7,"dname":"Force Staff","cost":2200,"components":["staff_of_wizardry","fluffy_hat"]}}
                """,
            _ => throw new InvalidOperationException(request.ToString()),
        });
        var build = await new OpenDotaService(_cache, client).GetLatestHeroBuildAsync(
            "456", "pudge", "100",
            matchPurchases: (matchId, accountId, _) =>
            {
                Assert.Equal(90, matchId);
                Assert.Equal(456, accountId);
                return Task.FromResult<IReadOnlyList<StratzItemPurchase>?>(
                    [new(5, 1500), new(999, 20), new(7, 900)]);
            });

        Assert.Equal(new[] { "Force Staff", "Black King Bar" }, build.Items.Select(item => item.Name));
        Assert.Equal(new int?[] { 900, 1500 }, build.Items.Select(item => item.PurchaseTimeSeconds));
        Assert.EndsWith(" · Purchase timeline", build.HeroMatchLabel);
    }

    [Theory]
    [InlineData("1", "Pos 1 · Carry (estimated)")]
    [InlineData("2", "Pos 2 · Mid (estimated)")]
    [InlineData("3", "Pos 3 · Offlane (estimated)")]
    [InlineData("4", "Pos 4 · Soft support (estimated)")]
    [InlineData("null", "Position unavailable")]
    [InlineData("0", "Position unavailable")]
    [InlineData("5", "Pos 5 · Hard support (estimated)")]
    [InlineData("6", "Position unavailable")]
    [InlineData("\"2\"", "Position unavailable")]
    public async Task LatestHeroMatchShowsEstimatedPositionForCorrectPlayer(string position, string expected)
    {
        using var client = Client(request => request.AbsolutePath switch
        {
            "/api/heroes" => """[{"id":14,"name":"npc_dota_hero_pudge","localized_name":"Pudge"}]""",
            "/api/players/456/matches" => CheckHeroFilter(request),
            "/api/matches/90" => """
                {"players":[{"account_id":777,"hero_id":14,"position_est":2},
                {"account_id":456,"hero_id":1,"position_est":2},
                {"account_id":456,"hero_id":14,"lane_role":2,"position_est":POSITION}]}
                """.Replace("POSITION", position),
            "/api/constants/items" => "{}",
            _ => throw new InvalidOperationException(request.ToString()),
        });
        var build = await new OpenDotaService(_cache, client).GetLatestHeroBuildAsync("456", "pudge", "100");
        Assert.Contains(expected, build.HeroMatchLabel);
        Assert.Equal(expected, build.PositionLabel);
    }

    [Theory]
    [InlineData(456, 1, null, null)]
    [InlineData(457, 2, null, null)]
    [InlineData(458, 3, null, null)]
    [InlineData(459, 4, null, null)]
    [InlineData(460, 5, null, null)]
    [InlineData(456, 3, 3, null)]
    [InlineData(458, 1, 1, null)]
    [InlineData(459, 5, 1, null)]
    [InlineData(460, 4, 3, null)]
    [InlineData(456, 5, null, 5)]
    public async Task UnparsedMatchEstimatesPositionFromOwnTeamFarm(
        int accountId, int expectedPosition, int? lane, int? suppliedPosition)
    {
        using var client = Client(request => request.AbsolutePath switch
        {
            "/api/heroes" => """[{"id":14,"name":"npc_dota_hero_pudge","localized_name":"Pudge"}]""",
            var path when path == $"/api/players/{accountId}/matches" => CheckHeroFilter(request),
            "/api/matches/90" => """
                {"players":[
                {"account_id":456,"hero_id":14,"player_slot":128,"gold_per_min":650,"last_hits":350},
                {"account_id":457,"hero_id":14,"player_slot":129,"gold_per_min":600,"last_hits":300},
                {"account_id":458,"hero_id":14,"player_slot":130,"gold_per_min":500,"last_hits":200},
                {"account_id":459,"hero_id":14,"player_slot":131,"gold_per_min":350,"last_hits":60},
                {"account_id":460,"hero_id":14,"player_slot":132,"gold_per_min":280,"last_hits":20},
                {"account_id":777,"hero_id":14,"player_slot":0,"gold_per_min":900,"last_hits":500}]}
                """.Replace($"\"account_id\":{accountId},",
                    $"\"account_id\":{accountId},\"lane_role\":{lane?.ToString() ?? "null"},\"position_est\":{suppliedPosition?.ToString() ?? "null"},"),
            "/api/constants/items" => "{}",
            _ => throw new InvalidOperationException(request.ToString()),
        });
        var build = await new OpenDotaService(_cache, client)
            .GetLatestHeroBuildAsync(accountId.ToString(), "pudge", "100");
        Assert.Equal(expectedPosition, build.Position);
        Assert.Contains(suppliedPosition.HasValue ? "OpenDota parsed-match estimate" : "final team gold per minute",
            build.PositionToolTip);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StratzPositionReplacesFarmEstimateAndFallsBackWhenMissing(bool stratzHasMatch)
    {
        // Venge out-farms Lich, but STRATZ knows Lich played mid.
        using var client = Client(request => request.AbsolutePath switch
        {
            "/api/heroes" => """[{"id":20,"name":"npc_dota_hero_vengefulspirit","localized_name":"Vengeful Spirit"}]""",
            "/api/players/457/matches" => """[{"match_id":90,"hero_id":20,"start_time":1700000000,"player_slot":131}]""",
            "/api/matches/90" => """
                {"players":[
                {"account_id":456,"hero_id":44,"player_slot":128,"gold_per_min":2046,"last_hits":513},
                {"account_id":457,"hero_id":20,"player_slot":129,"gold_per_min":1650,"last_hits":312},
                {"account_id":458,"hero_id":2,"player_slot":130,"gold_per_min":1571,"last_hits":208},
                {"account_id":459,"hero_id":88,"player_slot":131,"gold_per_min":1302,"last_hits":146},
                {"account_id":460,"hero_id":31,"player_slot":132,"gold_per_min":1209,"last_hits":96}]}
                """,
            "/api/constants/items" => "{}",
            _ => throw new InvalidOperationException(request.ToString()),
        });
        long? requestedMatch = null;
        var build = await new OpenDotaService(_cache, client).GetLatestHeroBuildAsync(
            "457", "vengefulspirit", "100", default, (matchId, _) =>
            {
                requestedMatch = matchId;
                return Task.FromResult<IReadOnlyDictionary<long, int>?>(
                    stratzHasMatch ? new Dictionary<long, int> { [457] = 5, [460] = 2 } : null);
            });

        Assert.Equal(90, requestedMatch);
        Assert.Equal(stratzHasMatch ? 5 : 2, build.Position);
        Assert.Equal(!stratzHasMatch, build.IsPositionEstimated);
        Assert.Equal(stratzHasMatch ? "Pos 5 · Hard support" : "Pos 2 · Mid (estimated)", build.PositionLabel);
        Assert.Contains(stratzHasMatch ? "STRATZ" : "final team gold per minute", build.PositionToolTip);
        Assert.Contains(build.PositionLabel, build.HeroMatchLabel);
    }

    [Fact]
    public async Task FailingPositionSourceFallsBackToEstimate()
    {
        using var client = Client(request => request.AbsolutePath switch
        {
            "/api/heroes" => """[{"id":14,"name":"npc_dota_hero_pudge","localized_name":"Pudge"}]""",
            "/api/players/456/matches" => CheckHeroFilter(request),
            "/api/matches/90" => """{"players":[{"account_id":456,"hero_id":14,"position_est":3}]}""",
            "/api/constants/items" => "{}",
            _ => throw new InvalidOperationException(request.ToString()),
        });
        var build = await new OpenDotaService(_cache, client).GetLatestHeroBuildAsync(
            "456", "pudge", "100", default, (_, _) => throw new HttpRequestException("STRATZ down"));
        Assert.Equal(3, build.Position);
        Assert.True(build.IsPositionEstimated);
    }

    [Fact]
    public async Task MissingMatchDetailStillLinksLatestMatchWithoutInventingItems()
    {
        using var client = Client(request => request.AbsolutePath switch
        {
            "/api/heroes" => """[{"id":14,"name":"npc_dota_hero_pudge","localized_name":"Pudge"}]""",
            "/api/players/456/matches" => """[{"match_id":90,"hero_id":14,"start_time":1700000000}]""",
            "/api/matches/90" => null,
            _ => throw new InvalidOperationException(request.ToString()),
        });
        var build = await new OpenDotaService(_cache, client).GetLatestHeroBuildAsync("456", "pudge", "100");
        Assert.True(build.HasHeroMatch);
        Assert.Empty(build.Items);
        Assert.Equal("Position unavailable", build.PositionLabel);
        Assert.Contains("Item build unavailable", build.HeroMatchLabel);
    }

    [Fact]
    public async Task NoHeroAndSelfDoNotMakeNetworkRequests()
    {
        using var client = Client(request => throw new InvalidOperationException(request.ToString()));
        var service = new OpenDotaService(_cache, client);
        Assert.Equal("You", await service.GetEncounterLabelAsync("123", "123", "100"));
        var build = await service.GetLatestHeroBuildAsync("123", "", "100");
        Assert.False(build.HasHeroMatch);
        Assert.Equal("—", build.Kda);
        Assert.Contains("Choose a hero", build.HeroMatchLabel);
    }

    [Theory]
    [InlineData(0, "Just now")]
    [InlineData(60, "1 minute ago")]
    [InlineData(120, "2 minutes ago")]
    [InlineData(3600, "1 hour ago")]
    [InlineData(7200, "2 hours ago")]
    [InlineData(86400, "1 day ago")]
    [InlineData(172800, "2 days ago")]
    [InlineData(2592000, "1 month ago")]
    [InlineData(31536000, "1 year ago")]
    public void MatchTimestampUsesRelativeTime(int secondsAgo, string expected)
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1800000000);
        Assert.Equal(expected, Mango.Models.PlayerMatchHistory.FormatRelativeTime(now.AddSeconds(-secondsAgo), now));
    }

    private static string CheckHeroFilter(Uri uri)
    {
        Assert.Contains("hero_id=14", uri.Query);
        Assert.Contains("significant=0", uri.Query);
        Assert.Contains("limit=2", uri.Query);
        return """
            [{"match_id":100,"hero_id":14,"start_time":1700000100},
             {"match_id":90,"hero_id":14,"start_time":1700000000,"player_slot":128,
              "radiant_win":true,"duration":1800,"kills":3,"deaths":4,"assists":5}]
            """;
    }

    private static HttpClient Client(Func<Uri, string?> respond) =>
        new(new Handler(respond)) { BaseAddress = new Uri("https://api.opendota.com/api/") };

    private sealed class Handler(Func<Uri, string?> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = respond(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(json is null ? HttpStatusCode.NotFound : HttpStatusCode.OK)
            { Content = new StringContent(json ?? "") });
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_cache)) Directory.Delete(_cache, recursive: true);
    }
}
