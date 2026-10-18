using Mango.Models;

namespace Mango.Services;

public static class PlayerRanks
{
    public static bool CanOverride(bool isProfilePrivate, int? reportedRankTier) =>
        isProfilePrivate || reportedRankTier is not > 0;

    public static IReadOnlyList<int> Tiers { get; } = Enumerable.Range(1, 7)
        .SelectMany(medal => Enumerable.Range(1, 5).Select(stars => medal * 10 + stars))
        .Append(80).ToArray();

    public static PlayerRankOverride? Get(
        IReadOnlyDictionary<string, PlayerRankOverride> ranks, string? playerId, bool isProfilePrivate,
        int? reportedRankTier = null)
    {
        if (!CanOverride(isProfilePrivate, reportedRankTier))
        {
            return null;
        }

        var key = SteamIdConverter.ToAccountId(playerId)?.ToString();
        return key is not null && ranks.TryGetValue(key, out var rank) && Tiers.Contains(rank.RankTier)
            ? rank
            : null;
    }

    public static Dictionary<string, PlayerRankOverride> Normalize(Dictionary<string, PlayerRankOverride>? ranks)
    {
        var result = new Dictionary<string, PlayerRankOverride>();
        if (ranks is null)
        {
            return result;
        }

        foreach (var (id, rank) in ranks)
        {
            var accountId = SteamIdConverter.ToAccountId(id);
            if (accountId is not null && rank is not null && Tiers.Contains(rank.RankTier))
            {
                result[accountId.Value.ToString()] = rank;
            }
        }

        return result;
    }
}
