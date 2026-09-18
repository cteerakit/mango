namespace Mango.Models;

public static class MatchRosterMerger
{
    public static IReadOnlyList<MatchPlayer> Merge(
        IReadOnlyList<MatchPlayer> gsiPlayers,
        IReadOnlyList<MatchPlayer> externalPlayers)
    {
        if (externalPlayers.Count == 0)
        {
            return gsiPlayers;
        }

        if (gsiPlayers.Count >= externalPlayers.Count
            && CountIdentified(gsiPlayers) >= CountIdentified(externalPlayers))
        {
            return gsiPlayers;
        }

        var gsiBySteamId = gsiPlayers
            .Select(static player => (Key: NormalizeSteamKey(player.SteamId), Player: player))
            .Where(static entry => !string.IsNullOrWhiteSpace(entry.Key))
            .ToDictionary(static entry => entry.Key, static entry => entry.Player, StringComparer.OrdinalIgnoreCase);

        var merged = new List<MatchPlayer>(externalPlayers.Count);
        foreach (var external in externalPlayers)
        {
            var key = NormalizeSteamKey(external.SteamId);
            merged.Add(!string.IsNullOrWhiteSpace(key) && gsiBySteamId.TryGetValue(key, out var gsiPlayer)
                ? OverlayGsiPlayer(external, gsiPlayer)
                : external);
        }

        foreach (var gsiPlayer in gsiPlayers)
        {
            var key = NormalizeSteamKey(gsiPlayer.SteamId);
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            if (merged.All(player => !string.Equals(NormalizeSteamKey(player.SteamId), key, StringComparison.OrdinalIgnoreCase)))
            {
                merged.Add(gsiPlayer);
            }
        }

        return merged
            .OrderBy(static player => TeamSortOrder(player.TeamName))
            .ThenBy(static player => player.TeamSlot)
            .ThenBy(static player => player.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Playing GSI usually sends only the local player. Keep a previously fetched
    /// lobby unless the match id changed or the new payload is already richer.
    /// </summary>
    public static IReadOnlyList<MatchPlayer> RetainIfRicher(
        IReadOnlyList<MatchPlayer> incoming,
        IReadOnlyList<MatchPlayer> current,
        string incomingMatchId,
        string currentMatchId)
    {
        if (!IsSameMatch(currentMatchId, incomingMatchId))
        {
            return incoming;
        }

        if (CountIdentified(incoming) >= CountIdentified(current) || CountIdentified(current) <= 1)
        {
            return incoming;
        }

        return Merge(incoming, current);
    }

    public static bool IsSameMatch(string currentMatchId, string incomingMatchId)
    {
        if (PlayerNotesMatch.IsUsableMatchId(currentMatchId)
            && PlayerNotesMatch.IsUsableMatchId(incomingMatchId)
            && !string.Equals(currentMatchId, incomingMatchId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    public static int CountIdentified(IReadOnlyList<MatchPlayer> players) =>
        players.Count(static player =>
            !string.IsNullOrWhiteSpace(player.SteamId) || !string.IsNullOrWhiteSpace(player.Name));

    private static MatchPlayer OverlayGsiPlayer(MatchPlayer external, MatchPlayer gsi)
    {
        return new MatchPlayer
        {
            SteamId = FirstNonEmpty(gsi.SteamId, external.SteamId),
            Name = FirstNonEmpty(gsi.Name, external.Name),
            TeamName = FirstNonEmpty(gsi.TeamName, external.TeamName),
            TeamSlot = gsi.TeamSlot > 0 ? gsi.TeamSlot : external.TeamSlot,
            HeroName = FirstNonEmpty(gsi.HeroName, external.HeroName),
            HeroLevel = gsi.HeroLevel > 0 ? gsi.HeroLevel : external.HeroLevel,
            Kills = gsi.Kills > 0 ? gsi.Kills : external.Kills,
            Deaths = gsi.Deaths > 0 ? gsi.Deaths : external.Deaths,
            Assists = gsi.Assists > 0 ? gsi.Assists : external.Assists,
            LastHits = gsi.LastHits > 0 ? gsi.LastHits : external.LastHits,
            Denies = gsi.Denies > 0 ? gsi.Denies : external.Denies,
            Gpm = gsi.Gpm > 0 ? gsi.Gpm : external.Gpm,
            Xpm = gsi.Xpm > 0 ? gsi.Xpm : external.Xpm,
            Gold = gsi.Gold > 0 ? gsi.Gold : external.Gold,
        };
    }

    private static int TeamSortOrder(string teamName)
    {
        if (teamName.Contains("radiant", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (teamName.Contains("dire", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        return 2;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static string NormalizeSteamKey(string? steamId)
    {
        var accountId = SteamIdConverter.ToAccountId(steamId);
        return accountId?.ToString() ?? steamId?.Trim() ?? string.Empty;
    }
}
