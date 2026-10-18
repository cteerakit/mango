using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Mango.Models;

public static class SteamLocalRosterMapper
{
    public static string? FindServer(string consoleText, string matchId)
    {
        if (!PlayerNotesMatch.IsUsableMatchId(matchId))
        {
            return null;
        }

        string? server = null;
        foreach (Match match in Regex.Matches(consoleText,
                     @"Received Steam datagram ticket for server steamid:(\d+)[^\r\n]*\bmatch_id=(\d+)\b"))
        {
            if (match.Groups[2].Value == matchId)
            {
                server = match.Groups[1].Value;
            }
        }

        return server;
    }

    public static IReadOnlyList<MatchPlayer> ReadPlayers(string json, string serverSteamId, string localSteamId)
    {
        var payload = JsonSerializer.Deserialize<LocalRoster>(json);
        if (payload?.ServerSteamId != serverSteamId || payload.Players is not { Count: > 1 and <= 10 })
        {
            return [];
        }

        var localAccount = SteamIdConverter.ToAccountId(localSteamId);
        if (localAccount is null || !payload.Players.Any(player => SteamIdConverter.ToAccountId(player.SteamId) == localAccount))
        {
            return [];
        }

        return payload.Players
            .Where(player => SteamIdConverter.ToAccountId(player.SteamId) is not null)
            .DistinctBy(player => SteamIdConverter.ToAccountId(player.SteamId))
            .Select(player => new MatchPlayer
            {
                SteamId = player.SteamId,
                Name = string.IsNullOrWhiteSpace(player.Name) ? player.SteamId : player.Name,
            })
            .ToList();
    }

    private sealed class LocalRoster
    {
        [JsonPropertyName("serverSteamId")]
        public string ServerSteamId { get; set; } = string.Empty;
        [JsonPropertyName("players")]
        public List<LocalPlayer>? Players { get; set; }
    }

    private sealed class LocalPlayer
    {
        [JsonPropertyName("steamId")]
        public string SteamId { get; set; } = string.Empty;
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;
    }
}
