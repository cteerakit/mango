using System.Text.Json;
using System.Text.Json.Serialization;
using Mango.Models.Gsi;

namespace Mango.Models;

public sealed class SteamTopLiveHit
{
    public string ServerSteamId { get; init; } = string.Empty;
    public string MatchId { get; init; } = string.Empty;
    public IReadOnlyList<MatchPlayer> Players { get; init; } = [];
}

public static class SteamTopLiveMapper
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static SteamTopLiveHit? FindGame(
        SteamTopLiveResponse? response,
        ulong? accountId,
        string? matchId,
        IReadOnlyDictionary<int, string>? heroNames = null)
    {
        foreach (var game in EnumerateGames(response))
        {
            if (Matches(game, accountId, matchId))
            {
                return ToHit(game, heroNames);
            }
        }

        return null;
    }

    public static SteamTopLiveHit ToHit(
        SteamTopLiveGame game,
        IReadOnlyDictionary<int, string>? heroNames = null) =>
        new()
        {
            ServerSteamId = game.ServerSteamId?.Trim() ?? string.Empty,
            MatchId = game.MatchId?.Trim() ?? string.Empty,
            Players = ToPlayers(game, heroNames),
        };

    public static IReadOnlyList<MatchPlayer> ToPlayers(
        SteamTopLiveGame game,
        IReadOnlyDictionary<int, string>? heroNames = null)
    {
        var source = game.Players ?? [];
        var players = new List<MatchPlayer>(source.Count);
        // The live list carries no team data and its order is not team order, so sides stay unknown.
        foreach (var player in source)
        {
            if (player.AccountId == 0)
            {
                continue;
            }

            string? heroName = null;
            heroNames?.TryGetValue(player.HeroId, out heroName);
            players.Add(new MatchPlayer
            {
                SteamId = player.AccountId.ToString(),
                Name = player.AccountId.ToString(),
                HeroName = heroName ?? string.Empty,
            });
        }

        return players;
    }

    private static IEnumerable<SteamTopLiveGame> EnumerateGames(SteamTopLiveResponse? response)
    {
        var games = response?.GameList ?? response?.Result?.GameList;
        if (games is { Count: > 0 })
        {
            foreach (var game in games)
            {
                yield return game;
            }
        }
    }

    private static bool Matches(SteamTopLiveGame game, ulong? accountId, string? matchId)
    {
        if (PlayerNotesMatch.IsUsableMatchId(matchId)
            && PlayerNotesMatch.IsUsableMatchId(game.MatchId))
        {
            return string.Equals(game.MatchId, matchId, StringComparison.OrdinalIgnoreCase);
        }

        return accountId is > 0
            && game.Players?.Any(player => player.AccountId == accountId.Value) == true;
    }
}

public sealed class SteamTopLiveResponse
{
    [JsonPropertyName("game_list")]
    public List<SteamTopLiveGame>? GameList { get; set; }

    [JsonPropertyName("result")]
    public SteamTopLiveResponse? Result { get; set; }
}

public sealed class SteamTopLiveGame
{
    [JsonPropertyName("server_steam_id")]
    [JsonConverter(typeof(GsiFlexibleStringJsonConverter))]
    public string? ServerSteamId { get; set; }

    [JsonPropertyName("match_id")]
    [JsonConverter(typeof(GsiFlexibleStringJsonConverter))]
    public string? MatchId { get; set; }

    [JsonPropertyName("players")]
    public List<SteamTopLivePlayer>? Players { get; set; }
}

public sealed class SteamTopLivePlayer
{
    [JsonPropertyName("account_id")]
    public ulong AccountId { get; set; }

    [JsonPropertyName("hero_id")]
    public int HeroId { get; set; }
}
