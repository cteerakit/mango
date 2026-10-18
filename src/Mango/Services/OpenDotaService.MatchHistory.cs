using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mango.Models;

namespace Mango.Services;

public sealed partial class OpenDotaService
{
    /// <summary>Match id each player's recent matches were last force-refreshed for.</summary>
    private readonly ConcurrentDictionary<ulong, string> _recentRefreshedForMatch = new();

    /// <summary>Item keys left out of the last-match build (e.g. Oblivion Staff).</summary>
    private static readonly HashSet<string> HiddenBuildItemKeys = new(StringComparer.Ordinal) { "oblivion_staff" };

    public async Task<(IReadOnlyList<RecentMatchSummary> Matches, string Label)> GetRecentMatchSummariesAsync(
        string steamId, string excludedMatchId, CancellationToken cancellationToken = default)
    {
        var accountId = SteamIdConverter.ToAccountId(steamId);
        if (accountId is null) return ([], "Recent matches unavailable.");

        // A new match means the previous one just finished, so skip the cache once per player.
        var refresh = PlayerNotesMatch.IsUsableMatchId(excludedMatchId)
            && (!_recentRefreshedForMatch.TryGetValue(accountId.Value, out var refreshedFor)
                || !string.Equals(refreshedFor, excludedMatchId, StringComparison.Ordinal));
        var matches = await GetCachedAsync<List<OpenDotaRecentMatch>>(
            $"player_{accountId}_recent.json", () => $"players/{accountId}/recentMatches",
            cancellationToken, refresh ? TimeSpan.Zero : TimeSpan.FromMinutes(15));
        if (refresh) _recentRefreshedForMatch[accountId.Value] = excludedMatchId;
        if (matches is null) return ([], "Recent matches unavailable.");
        var heroes = await GetHeroesAsync(cancellationToken);
        var recent = matches.Where(match => match.MatchId > 0 && match.MatchId.ToString() != excludedMatchId)
            .OrderByDescending(match => match.StartTime).Take(5)
            .Select(match =>
            {
                var hero = heroes.FirstOrDefault(hero => hero.Id == match.HeroId);
                var heroKey = hero?.Name.Replace("npc_dota_hero_", string.Empty, StringComparison.OrdinalIgnoreCase);
                return new RecentMatchSummary
                {
                    HeroName = hero?.LocalizedName ?? $"Hero {match.HeroId}",
                    HeroImageUrl = string.IsNullOrEmpty(heroKey) ? string.Empty
                        : $"https://cdn.cloudflare.steamstatic.com/apps/dota2/images/dota_react/heroes/{heroKey}.png",
                    Result = (match.PlayerSlot < 128) == match.RadiantWin ? "Win" : "Loss",
                };
            }).ToList();
        return (recent, recent.Count == 0 ? "No recent matches recorded by OpenDota."
            : "Last 5 completed matches, newest first");
    }

    public async Task<string> GetEncounterLabelAsync(
        string localSteamId, string playerSteamId, string excludedMatchId,
        CancellationToken cancellationToken = default)
    {
        var localId = SteamIdConverter.ToAccountId(localSteamId);
        var playerId = SteamIdConverter.ToAccountId(playerSteamId);
        if (localId is null) return "Your account is needed for encounter history.";
        if (playerId is null) return "Encounter history unavailable.";
        if (localId == playerId) return "You";

        var withTask = GetCachedAsync<List<OpenDotaRecentMatch>>(
            $"encounters_{localId}_{playerId}_with.json",
            () => $"players/{localId}/matches?with_account_id={playerId}&significant=0",
            cancellationToken, TimeSpan.FromMinutes(15));
        var againstTask = GetCachedAsync<List<OpenDotaRecentMatch>>(
            $"encounters_{localId}_{playerId}_against.json",
            () => $"players/{localId}/matches?against_account_id={playerId}&significant=0",
            cancellationToken, TimeSpan.FromMinutes(15));
        await Task.WhenAll(withTask, againstTask);
        var with = await withTask;
        var against = await againstTask;
        if (with is null || against is null) return "Encounter history unavailable.";

        var withCount = with.Count(match => match.MatchId.ToString() != excludedMatchId);
        var againstCount = against.Count(match => match.MatchId.ToString() != excludedMatchId);
        return $"{withCount:N0}/{againstCount:N0}";
    }

    public async Task<PlayerMatchHistory> GetLatestHeroBuildAsync(
        string steamId, string heroName, string excludedMatchId,
        CancellationToken cancellationToken = default,
        Func<long, CancellationToken, Task<IReadOnlyDictionary<long, int>?>>? matchPositions = null,
        Func<long, long, CancellationToken, Task<IReadOnlyList<StratzItemPurchase>?>>? matchPurchases = null)
    {
        if (string.IsNullOrWhiteSpace(heroName))
            return new() { HeroMatchLabel = "Choose a hero to see their latest build." };
        var accountId = SteamIdConverter.ToAccountId(steamId);
        if (accountId is null)
            return new() { HeroMatchLabel = "Hero match history unavailable." };
        var heroes = await GetHeroesAsync(cancellationToken);
        var normalizedName = heroName.StartsWith("npc_dota_hero_", StringComparison.OrdinalIgnoreCase)
            ? heroName["npc_dota_hero_".Length..] : heroName;
        var heroId = ResolveHeroId(normalizedName, heroes);
        if (heroId is null)
            return new() { HeroMatchLabel = "Hero match history unavailable." };

        var matches = await GetCachedAsync<List<OpenDotaRecentMatch>>(
            $"player_{accountId}_hero_{heroId}_latest.json",
            () => $"players/{accountId}/matches?hero_id={heroId}&limit=2&significant=0&sort=start_time",
            cancellationToken, TimeSpan.FromMinutes(15));
        if (matches is null)
            return new() { HeroMatchLabel = "Hero match history unavailable." };
        var latest = matches.Where(match => match.MatchId > 0 && match.HeroId == heroId
                && match.MatchId.ToString() != excludedMatchId)
            .OrderByDescending(match => match.StartTime).FirstOrDefault();
        if (latest is null)
            return new() { HeroMatchLabel = "No past matches on this hero recorded by OpenDota." };

        var detail = await GetCachedAsync<HeroBuildMatch>(
            $"match_{latest.MatchId}_build.json", () => $"matches/{latest.MatchId}",
            cancellationToken, TimeSpan.FromHours(6));
        var player = detail?.Players?.FirstOrDefault(row =>
            row.TryGetProperty("account_id", out var id) && id.TryGetUInt64(out var value) && value == accountId
            && row.TryGetProperty("hero_id", out var hero) && hero.TryGetInt32(out var valueHero) && valueHero == heroId);
        var items = new List<MatchBuildItem>();
        int? position = null;
        var positionBasis = "OpenDota parsed-match estimate.";
        var isPositionEstimated = true;
        IReadOnlyDictionary<long, int>? reportedPositions = null;
        if (matchPositions is not null)
        {
            try { reportedPositions = await matchPositions(latest.MatchId, cancellationToken); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { /* Fall back to OpenDota when the position source fails. */ }
        }
        if (reportedPositions?.TryGetValue((long)accountId.Value, out var reported) == true && reported is >= 1 and <= 5)
        {
            position = reported;
            positionBasis = "Position from STRATZ replay analysis.";
            isPositionEstimated = false;
        }
        if (player is { ValueKind: JsonValueKind.Object })
        {
            if (position is null && player.Value.TryGetProperty("position_est", out var estimate)
                && estimate.ValueKind == JsonValueKind.Number && estimate.TryGetInt32(out var positionNumber)
                && positionNumber is >= 1 and <= 5)
            {
                position = positionNumber;
            }
            else if (position is null && detail?.Players is { } teammates)
            {
                position = EstimatePositionFromFarm(teammates, player.Value);
                positionBasis = "Estimated from final team gold per minute and last hits, using lanes when available. May differ from the role played.";
            }
            var constants = await GetCachedAsync<Dictionary<string, BuildItemConstant>>(
                "build_item_constants.json", () => "constants/items", cancellationToken, TimeSpan.FromDays(1));
            var purchases = new List<(string Key, int Time)>();
            if (player.Value.TryGetProperty("purchase_log", out var log) && log.ValueKind == JsonValueKind.Array)
            {
                foreach (var purchase in log.EnumerateArray())
                {
                    if (purchase.ValueKind == JsonValueKind.Object
                        && purchase.TryGetProperty("key", out var key) && key.ValueKind == JsonValueKind.String
                        && purchase.TryGetProperty("time", out var time) && time.ValueKind == JsonValueKind.Number
                        && time.TryGetInt32(out var seconds))
                    {
                        purchases.Add((key.GetString()!, seconds));
                    }
                }
            }
            if (purchases.Count == 0 && matchPurchases is not null && constants is not null)
            {
                // Unparsed OpenDota matches have no purchase_log; STRATZ has the timeline right away.
                try
                {
                    var stratzPurchases = await matchPurchases(latest.MatchId, (long)accountId.Value, cancellationToken);
                    var keysById = constants.Where(pair => pair.Value.Id > 0)
                        .GroupBy(pair => pair.Value.Id).ToDictionary(group => group.Key, group => group.First().Key);
                    foreach (var purchase in stratzPurchases ?? [])
                    {
                        if (keysById.TryGetValue(purchase.ItemId, out var purchaseKey))
                            purchases.Add((purchaseKey, purchase.Time));
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { /* Fall back to the final inventory when STRATZ fails. */ }
            }
            foreach (var purchase in purchases.OrderBy(purchase => purchase.Time))
            {
                var constant = constants?.GetValueOrDefault(purchase.Key);
                if (!IsKeyBuildItem(purchase.Key, constant)) continue;
                items.Add(new MatchBuildItem
                {
                    Name = constant?.DisplayName ?? purchase.Key,
                    ImageUrl = $"https://cdn.cloudflare.steamstatic.com/apps/dota2/images/dota_react/items/{purchase.Key}.png",
                    Slot = "Purchase",
                    PurchaseTimeSeconds = purchase.Time,
                });
            }
            foreach (var slot in new[] { "item_0", "item_1", "item_2", "item_3", "item_4", "item_5",
                         "backpack_0", "backpack_1", "backpack_2", "item_neutral" })
            {
                if (purchases.Count > 0) break;
                if (!player.Value.TryGetProperty(slot, out var item) || !item.TryGetInt32(out var itemId) || itemId <= 0)
                    continue;
                var constant = constants?.FirstOrDefault(pair => pair.Value.Id == itemId);
                var key = constant?.Key;
                if (key is null || !IsKeyBuildItem(key, constant?.Value)) continue;
                items.Add(new MatchBuildItem
                {
                    Name = constant?.Value?.DisplayName ?? $"Item {itemId}",
                    ImageUrl = key is null ? string.Empty
                        : $"https://cdn.cloudflare.steamstatic.com/apps/dota2/images/dota_react/items/{key}.png",
                    Slot = slot.StartsWith("backpack", StringComparison.Ordinal) ? "Backpack"
                        : slot == "item_neutral" ? "Neutral" : "Inventory",
                });
            }
        }

        var row = ToMatchRow(latest, heroes.ToDictionary(hero => hero.Id, hero => hero.LocalizedName));
        return new PlayerMatchHistory
        {
            Kda = row.Kda,
            Result = row.Result,
            Position = position,
            PositionBasis = positionBasis,
            IsPositionEstimated = isPositionEstimated,
            PlayedAt = latest.StartTime > 0 ? DateTimeOffset.FromUnixTimeSeconds(latest.StartTime) : null,
            HeroMatchLabel = $"Latest {row.HeroName} · {row.Result} · {row.Kda} · {PlayerMatchHistory.FormatPositionLabel(position, isPositionEstimated)} · {row.Duration} · {row.PlayedAt}"
                + (items.Count == 0 ? " · Item build unavailable"
                    : items.Any(item => item.Slot == "Purchase") ? " · Purchase timeline" : " · Final items"),
            HeroMatchUrl = $"https://www.opendota.com/matches/{latest.MatchId}",
            Items = items.OrderBy(item => item.PurchaseTimeSeconds ?? int.MaxValue).ToList(),
        };
    }

    private static int? EstimatePositionFromFarm(IReadOnlyList<JsonElement> players, JsonElement player)
    {
        var slot = ReadMatchInt(player, "player_slot");
        if (slot is null) return null;
        var team = players.Where(row => ReadMatchInt(row, "player_slot") is { } otherSlot
                && (otherSlot < 128) == (slot < 128))
            .ToList();
        if (team.Count != 5 || team.Select(row => ReadMatchInt(row, "player_slot")).Distinct().Count() != 5
            || team.Any(row => ReadMatchInt(row, "gold_per_min") is null or <= 0
                || ReadMatchInt(row, "last_hits") is null or < 0)) return null;

        team = team.OrderByDescending(row => ReadMatchInt(row, "gold_per_min"))
            .ThenByDescending(row => ReadMatchInt(row, "last_hits")).ToList();
        var assignments = new Dictionary<int, int>();
        foreach (var (group, positions) in new[] { (team.Take(3), new[] { 1, 2, 3 }), (team.Skip(3), new[] { 4, 5 }) })
        {
            var remaining = positions.ToList();
            var unresolved = new List<JsonElement>();
            foreach (var row in group)
            {
                var lane = ReadMatchInt(row, "lane_role");
                var preferred = positions[0] == 1 ? lane : lane switch { 1 => 5, 2 or 3 => 4, _ => null };
                if (preferred is { } value && remaining.Remove(value))
                    assignments[ReadMatchInt(row, "player_slot")!.Value] = value;
                else
                    unresolved.Add(row);
            }
            for (var i = 0; i < unresolved.Count; i++)
                assignments[ReadMatchInt(unresolved[i], "player_slot")!.Value] = remaining[i];
        }
        return assignments.GetValueOrDefault(slot.Value);
    }

    private static int? ReadMatchInt(JsonElement row, string property) =>
        row.ValueKind == JsonValueKind.Object && row.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    private static bool IsKeyBuildItem(string key, BuildItemConstant? item)
    {
        if (HiddenBuildItemKeys.Contains(key)) return false;
        return key is "blink" or "ghost" or "gem" or "ultimate_scepter_2" or "aghanims_shard"
            || (!key.StartsWith("recipe_", StringComparison.Ordinal)
                && item is { Cost: >= 1000, Components.Length: > 0 });
    }

    private sealed class HeroBuildMatch
    {
        [JsonPropertyName("players")]
        public List<JsonElement>? Players { get; set; }
    }

    private sealed class BuildItemConstant
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }
        [JsonPropertyName("dname")]
        public string? DisplayName { get; set; }
        [JsonPropertyName("cost")]
        public int? Cost { get; set; }
        [JsonPropertyName("components")]
        public string[]? Components { get; set; }
    }
}
