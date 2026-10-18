using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mango.Models;
using Mango.Models.Gsi;

namespace Mango.Services;

public sealed partial class OpenDotaService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private const int SlimMatchCacheBytes = 4096;

    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _matchDetailThrottle = new(4);
    private readonly string _cacheDirectory;
    private List<HeroConstant>? _heroes;

    public OpenDotaService(string cacheDirectory, HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient
        {
            BaseAddress = new Uri("https://api.opendota.com/api/"),
            Timeout = TimeSpan.FromSeconds(20),
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mango/1.0");

        _cacheDirectory = cacheDirectory;
        Directory.CreateDirectory(_cacheDirectory);
    }

    public async Task<IReadOnlyList<HeroConstant>> GetHeroesAsync(CancellationToken cancellationToken = default)
    {
        if (_heroes is not null)
        {
            return _heroes;
        }

        _heroes = await GetCachedRequiredAsync<List<HeroConstant>>(
            "heroes.json",
            static () => "heroes",
            cancellationToken);
        return _heroes;
    }

    public async Task<IReadOnlyList<HeroMatchup>> GetHeroMatchupsAsync(int heroId, CancellationToken cancellationToken = default)
    {
        return await GetCachedRequiredAsync<List<HeroMatchup>>(
            $"matchups_{heroId}.json",
            () => $"heroes/{heroId}/matchups",
            cancellationToken,
            TimeSpan.FromDays(1));
    }

    public async Task<ItemPopularity?> GetItemPopularityAsync(int heroId, CancellationToken cancellationToken = default)
    {
        return await GetCachedAsync<ItemPopularity>(
            $"item_popularity_{heroId}.json",
            () => $"heroes/{heroId}/itemPopularity",
            cancellationToken,
            TimeSpan.FromDays(1));
    }

    public async Task<IReadOnlyList<CounterSuggestion>> GetCounterSuggestionsAsync(
        IEnumerable<int> enemyHeroIds,
        CancellationToken cancellationToken = default)
    {
        var heroes = await GetHeroesAsync(cancellationToken);
        var enemyIds = enemyHeroIds.Where(static id => id > 0).Distinct().ToList();
        if (enemyIds.Count == 0)
        {
            return Array.Empty<CounterSuggestion>();
        }

        var scores = new Dictionary<int, double>();
        foreach (var enemyId in enemyIds)
        {
            var matchups = await GetHeroMatchupsAsync(enemyId, cancellationToken);
            foreach (var matchup in matchups)
            {
                if (!scores.ContainsKey(matchup.HeroId))
                {
                    scores[matchup.HeroId] = 0;
                }

                scores[matchup.HeroId] += matchup.Wins / Math.Max(matchup.GamesPlayed, 1.0);
            }
        }

        return scores
            .Where(kv => !enemyIds.Contains(kv.Key))
            .OrderByDescending(static kv => kv.Value)
            .Take(5)
            .Select(kv =>
            {
                var hero = heroes.FirstOrDefault(h => h.Id == kv.Key);
                return new CounterSuggestion
                {
                    HeroId = kv.Key,
                    HeroName = hero?.LocalizedName ?? $"Hero {kv.Key}",
                    Score = kv.Value,
                };
            })
            .ToList();
    }

    public async Task<IReadOnlyList<string>> GetSuggestedItemsAsync(int heroId, CancellationToken cancellationToken = default)
    {
        var popularity = await GetItemPopularityAsync(heroId, cancellationToken);
        if (popularity is null)
        {
            return Array.Empty<string>();
        }

        return popularity.StartGameItems
            .Concat(popularity.EarlyGameItems)
            .Concat(popularity.MidGameItems)
            .Concat(popularity.LateGameItems)
            .Distinct()
            .Take(8)
            .Select(ResolveItemName)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .ToList();
    }

    public string? GetHeroIdByInternalName(string internalHeroName, IReadOnlyList<HeroConstant> heroes)
    {
        if (string.IsNullOrWhiteSpace(internalHeroName))
        {
            return null;
        }

        var hero = heroes.FirstOrDefault(h =>
            string.Equals(h.Name, $"npc_dota_hero_{internalHeroName}", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(h.LocalizedName, internalHeroName, StringComparison.OrdinalIgnoreCase));

        return hero?.Id.ToString();
    }

    public int? ResolveHeroId(string internalHeroName, IReadOnlyList<HeroConstant> heroes)
    {
        var idText = GetHeroIdByInternalName(internalHeroName, heroes);
        return int.TryParse(idText, out var id) ? id : null;
    }

    public async Task<IReadOnlyList<MatchPlayer>?> GetMatchRosterAsync(
        string matchId,
        ulong? localAccountId = null,
        CancellationToken cancellationToken = default)
    {
        if (!PlayerNotesMatch.IsUsableMatchId(matchId) && localAccountId is null)
        {
            return null;
        }

        var heroNames = await GetHeroInternalNameMapAsync(cancellationToken);
        // Live matches are not in matches/{id} until parsed. Waiting on that 404/522
        // path used to burn the roster timeout and discard Steam results.
        return await FetchLiveRosterAsync(matchId, localAccountId, heroNames, cancellationToken);
    }

    private async Task<IReadOnlyList<MatchPlayer>?> FetchLiveRosterAsync(
        string matchId,
        ulong? localAccountId,
        IReadOnlyDictionary<int, string> heroNames,
        CancellationToken cancellationToken)
    {
        var liveGame = await FindLiveGameAsync(matchId, localAccountId, cancellationToken);
        var livePlayers = liveGame?.Players;
        if (livePlayers is not { Count: > 0 })
        {
            return null;
        }

        return livePlayers
            .Where(static player => player.AccountId > 0)
            .Select(player => ToMatchPlayerFromLive(player, heroNames))
            .Where(static player => !string.IsNullOrWhiteSpace(player.SteamId))
            .ToList();
    }

    private async Task<OpenDotaLiveGame?> FindLiveGameAsync(
        string matchId,
        ulong? localAccountId,
        CancellationToken cancellationToken)
    {
        var liveGames = await GetCachedAsync<List<OpenDotaLiveGame>>(
            "live_games.json",
            static () => "live",
            cancellationToken,
            TimeSpan.FromSeconds(5));

        if (liveGames is null || liveGames.Count == 0)
        {
            return null;
        }

        if (PlayerNotesMatch.IsUsableMatchId(matchId))
        {
            var byMatchId = liveGames.FirstOrDefault(game =>
                string.Equals(game.MatchId, matchId, StringComparison.OrdinalIgnoreCase));
            if (byMatchId is not null)
            {
                return byMatchId;
            }
        }

        if (localAccountId is null)
        {
            return null;
        }

        return liveGames.FirstOrDefault(game =>
            (!PlayerNotesMatch.IsUsableMatchId(matchId) || !PlayerNotesMatch.IsUsableMatchId(game.MatchId))
            &&
            game.Players?.Any(player => player.AccountId == localAccountId.Value) == true);
    }

    private async Task<IReadOnlyDictionary<int, string>> GetHeroInternalNameMapAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var heroes = await GetHeroesAsync(cancellationToken);
            return heroes
                .GroupBy(static hero => hero.Id)
                .ToDictionary(static group => group.Key, static group => group.First().Name);
        }
        catch
        {
            return new Dictionary<int, string>();
        }
    }

    private static MatchPlayer ToMatchPlayerFromLive(
        OpenDotaLivePlayer player,
        IReadOnlyDictionary<int, string> heroNames)
    {
        var isRadiant = player.Team == 0;
        heroNames.TryGetValue(player.HeroId, out var heroName);

        return new MatchPlayer
        {
            SteamId = player.AccountId.ToString(),
            Name = player.AccountId.ToString(),
            TeamName = isRadiant ? "radiant" : "dire",
            TeamSlot = Math.Max(player.TeamSlot - 1, 0),
            HeroName = heroName ?? string.Empty,
        };
    }

    public async Task<PlayerOverview?> GetPlayerOverviewAsync(
        string steamId,
        CancellationToken cancellationToken = default)
    {
        var accountId = SteamIdConverter.ToAccountId(steamId);
        if (accountId is null)
        {
            return null;
        }

        var heroNamesTask = GetHeroNameMapAsync(cancellationToken);

        var profileTask = GetCachedAsync<OpenDotaPlayerProfile>(
            $"player_{accountId}.json",
            () => $"players/{accountId}",
            cancellationToken,
            TimeSpan.FromHours(6));

        var winLossTask = GetCachedAsync<OpenDotaWinLoss>(
            $"player_{accountId}_wl_all_modes.json",
            () => $"players/{accountId}/wl?significant=0",
            cancellationToken,
            TimeSpan.FromHours(1));

        var recentMatchesTask = GetCachedAsync<List<OpenDotaRecentMatch>>(
            $"player_{accountId}_recent.json",
            () => $"players/{accountId}/recentMatches",
            cancellationToken,
            TimeSpan.FromMinutes(15));

        var rankedMatchesTask = GetCachedAsync<List<OpenDotaRecentMatch>>(
            $"player_{accountId}_ranked_recent.json",
            () => $"players/{accountId}/matches?lobby_type=7&limit=10&sort=start_time&significant=0",
            cancellationToken,
            TimeSpan.Zero);

        await Task.WhenAll(heroNamesTask, profileTask, winLossTask, recentMatchesTask, rankedMatchesTask);
        var heroNames = await heroNamesTask;
        var profile = await profileTask;
        var winLoss = await winLossTask;
        var recentMatches = await recentMatchesTask;
        var rankedMatches = await rankedMatchesTask;

        if (profile is null && winLoss is null && recentMatches is null && rankedMatches is null)
        {
            return null;
        }

        var wins = winLoss?.Win ?? 0;
        var losses = winLoss?.Lose ?? 0;
        var total = wins + losses;
        var mmrEstimate = profile?.MmrEstimate?.Estimate is > 0 ? profile.MmrEstimate.Estimate : null;
        var isPrivate = profile?.Profile?.FhUnavailable == true;
        var historyEstimate = profile?.RankTier is > 0 && !isPrivate
            ? null
            : await EstimateFromTeammateRanksAsync(rankedMatches ?? [], cancellationToken);
        var resolved = RankEstimator.Resolve(
            profile?.RankTier,
            profile?.LeaderboardRank,
            historyEstimate,
            mmrEstimate,
            lobbyAverageTiers: null,
            isProfilePrivate: isPrivate);

        return new PlayerOverview
        {
            SteamId = steamId.Trim(),
            DisplayName = FirstNonEmpty(profile?.Profile?.PersonaName, steamId),
            RankTier = profile?.RankTier,
            LeaderboardRank = profile?.LeaderboardRank,
            MmrEstimate = mmrEstimate,
            HistoryRankEstimate = historyEstimate,
            DisplayRankTier = resolved.RankTier,
            RankIsEstimated = resolved.IsEstimated,
            RankLabel = string.IsNullOrWhiteSpace(resolved.Label) ? "—" : resolved.Label,
            Wins = wins,
            Losses = losses,
            WinRatePercent = total > 0 ? Math.Round(wins * 100.0 / total, 1) : null,
            IsPrivate = isPrivate,
            RecentMatches = recentMatches?
                .Select(match => ToMatchRow(match, heroNames))
                .ToList() ?? [],
        };
    }

    private async Task<RankEstimate?> EstimateFromTeammateRanksAsync(
        IReadOnlyList<OpenDotaRecentMatch> rankedMatches,
        CancellationToken cancellationToken)
    {
        var candidates = rankedMatches
            .Where(static match => match.MatchId > 0 && match.AverageRank is > 0)
            .ToList();
        var matches = await Task.WhenAll(candidates.Select(async match =>
            (match, await GetMatchPlayerRanksAsync(match.MatchId, cancellationToken))));
        return RankEstimator.EstimateFromTeammateRanks(matches);
    }

    private async Task<IReadOnlyList<OpenDotaMatchPlayer>?> GetMatchPlayerRanksAsync(
        long matchId,
        CancellationToken cancellationToken)
    {
        await _matchDetailThrottle.WaitAsync(cancellationToken);
        try
        {
            var cacheFile = $"match_{matchId}_ranks.json";
            var detail = await GetCachedAsync<OpenDotaMatchDetail>(cacheFile, () => $"matches/{matchId}", cancellationToken);
            var cachePath = Path.Combine(_cacheDirectory, cacheFile);
            if (detail is not null && new FileInfo(cachePath) is { Exists: true, Length: > SlimMatchCacheBytes })
            {
                // Completed matches never change; keep only the slots and ranks the estimate needs.
                await File.WriteAllTextAsync(cachePath, JsonSerializer.Serialize(detail), cancellationToken);
            }

            return detail?.Players;
        }
        finally
        {
            _matchDetailThrottle.Release();
        }
    }

    private async Task<IReadOnlyDictionary<int, string>> GetHeroNameMapAsync(CancellationToken cancellationToken)
    {
        try
        {
            var heroes = await GetHeroesAsync(cancellationToken);
            return heroes
                .GroupBy(static hero => hero.Id)
                .ToDictionary(static group => group.Key, static group => group.First().LocalizedName);
        }
        catch
        {
            return new Dictionary<int, string>();
        }
    }

    private static PlayerMatchRow ToMatchRow(
        OpenDotaRecentMatch match,
        IReadOnlyDictionary<int, string> heroNames)
    {
        var isRadiant = match.PlayerSlot < 128;
        var won = isRadiant == match.RadiantWin;
        heroNames.TryGetValue(match.HeroId, out var heroName);
        heroName ??= $"Hero {match.HeroId}";

        return new PlayerMatchRow
        {
            MatchId = match.MatchId.ToString(),
            HeroName = heroName,
            Result = won ? "Win" : "Loss",
            Kda = $"{match.Kills}/{match.Deaths}/{match.Assists}",
            Duration = FormatDuration(match.Duration),
            PlayedAt = DateTimeOffset.FromUnixTimeSeconds(match.StartTime).ToLocalTime().ToString("g"),
        };
    }

    private static string FormatDuration(int seconds)
    {
        if (seconds <= 0)
        {
            return "—";
        }

        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:D2}:{span.Seconds:D2}"
            : $"{span.Minutes}:{span.Seconds:D2}";
    }

    public static string FormatRankTier(int? rankTier, int? leaderboardRank = null)
    {
        if (rankTier is null or <= 0)
        {
            return "Unranked";
        }

        var tier = rankTier.Value;
        var medal = tier / 10;
        var stars = tier % 10;
        var medalName = medal switch
        {
            1 => "Herald",
            2 => "Guardian",
            3 => "Crusader",
            4 => "Archon",
            5 => "Legend",
            6 => "Ancient",
            7 => "Divine",
            8 => "Immortal",
            _ => "Unknown",
        };

        if (medal == 8)
        {
            return leaderboardRank is > 0 ? $"Immortal #{leaderboardRank}" : medalName;
        }

        return stars > 0 ? $"{medalName} {stars}" : medalName;
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

        return "—";
    }

    private static string ResolveItemName(int itemId) =>
        itemId switch
        {
            > 0 => $"item_{itemId}",
            _ => string.Empty,
        };

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _cacheLocks = new();
    private readonly SemaphoreSlim _requestThrottle = new(6);

    private async Task<T?> GetCachedAsync<T>(
        string cacheFile,
        Func<string> endpointFactory,
        CancellationToken cancellationToken,
        TimeSpan? maxAge = null) where T : class
    {
        var gate = _cacheLocks.GetOrAdd(cacheFile, static _ => new SemaphoreSlim(1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await GetCachedCoreAsync<T>(cacheFile, endpointFactory, cancellationToken, maxAge);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<T?> GetCachedCoreAsync<T>(
        string cacheFile,
        Func<string> endpointFactory,
        CancellationToken cancellationToken,
        TimeSpan? maxAge = null) where T : class
    {
        var cachePath = Path.Combine(_cacheDirectory, cacheFile);
        string? staleJson = null;
        if (File.Exists(cachePath))
        {
            staleJson = await File.ReadAllTextAsync(cachePath, cancellationToken);
            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath);
            if (maxAge is null || age <= maxAge)
            {
                var cachedValue = DeserializeCached<T>(staleJson);
                if (cachedValue is not null)
                {
                    return cachedValue;
                }
            }
        }

        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await _requestThrottle.WaitAsync(cancellationToken);
                HttpStatusCode statusCode;
                try
                {
                    using var response = await _httpClient.GetAsync(endpointFactory(), cancellationToken);
                    statusCode = response.StatusCode;
                    if (response.IsSuccessStatusCode)
                    {
                        var json = await response.Content.ReadAsStringAsync(cancellationToken);
                        await File.WriteAllTextAsync(cachePath, json, cancellationToken);
                        return DeserializeCached<T>(json);
                    }
                }
                finally
                {
                    _requestThrottle.Release();
                }

                if (!IsTransientStatusCode((int)statusCode) || attempt == 2)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromSeconds(1 << attempt), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            // Fall back to stale cache when the network is unavailable.
        }
        catch (TaskCanceledException)
        {
            // HttpClient timeout; fall back to stale cache.
        }

        return DeserializeCached<T>(staleJson);
    }

    private static T? DeserializeCached<T>(string? json) where T : class =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<T>(json, JsonOptions);

    private static bool IsTransientStatusCode(int statusCode) =>
        statusCode is (int)HttpStatusCode.RequestTimeout
            or (int)HttpStatusCode.TooManyRequests
            or (int)HttpStatusCode.BadGateway
            or (int)HttpStatusCode.ServiceUnavailable
            or (int)HttpStatusCode.GatewayTimeout
            or 522;

    private async Task<T> GetCachedRequiredAsync<T>(
        string cacheFile,
        Func<string> endpointFactory,
        CancellationToken cancellationToken,
        TimeSpan? maxAge = null) where T : class
    {
        var value = await GetCachedAsync<T>(cacheFile, endpointFactory, cancellationToken, maxAge);
        return value ?? throw new InvalidOperationException($"Failed to load OpenDota data from {endpointFactory()}");
    }
}

public sealed class HeroConstant
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("localized_name")]
    public string LocalizedName { get; set; } = string.Empty;
}

public sealed class HeroMatchup
{
    [JsonPropertyName("hero_id")]
    public int HeroId { get; set; }

    [JsonPropertyName("games_played")]
    public int GamesPlayed { get; set; }

    [JsonPropertyName("wins")]
    public int Wins { get; set; }
}

public sealed class ItemPopularity
{
    [JsonPropertyName("start_game_items")]
    public List<int> StartGameItems { get; set; } = [];

    [JsonPropertyName("early_game_items")]
    public List<int> EarlyGameItems { get; set; } = [];

    [JsonPropertyName("mid_game_items")]
    public List<int> MidGameItems { get; set; } = [];

    [JsonPropertyName("late_game_items")]
    public List<int> LateGameItems { get; set; } = [];
}

public sealed class CounterSuggestion
{
    public int HeroId { get; set; }
    public string HeroName { get; set; } = string.Empty;
    public double Score { get; set; }
}

public sealed class OpenDotaPlayerProfile
{
    [JsonPropertyName("profile")]
    public OpenDotaPlayerProfileDetails? Profile { get; set; }

    [JsonPropertyName("rank_tier")]
    public int? RankTier { get; set; }

    [JsonPropertyName("leaderboard_rank")]
    public int? LeaderboardRank { get; set; }

    /// <summary>Legacy MMR-scale estimate. <c>computed_mmr</c> is deliberately ignored: it is
    /// OpenDota's internal rating centred near 4000, not in-game MMR.</summary>
    [JsonPropertyName("mmr_estimate")]
    public OpenDotaMmrEstimate? MmrEstimate { get; set; }
}

public sealed class OpenDotaMmrEstimate
{
    [JsonPropertyName("estimate")]
    public int? Estimate { get; set; }
}

public sealed class OpenDotaPlayerProfileDetails
{
    [JsonPropertyName("personaname")]
    public string PersonaName { get; set; } = string.Empty;

    /// <summary>True when OpenDota cannot refresh match history (often Expose Public Match Data off).</summary>
    [JsonPropertyName("fh_unavailable")]
    public bool? FhUnavailable { get; set; }
}

public sealed class OpenDotaWinLoss
{
    [JsonPropertyName("win")]
    public int Win { get; set; }

    [JsonPropertyName("lose")]
    public int Lose { get; set; }
}

public sealed class OpenDotaLiveGame
{
    [JsonPropertyName("match_id")]
    [JsonConverter(typeof(GsiFlexibleStringJsonConverter))]
    public string MatchId { get; set; } = string.Empty;

    [JsonPropertyName("server_steam_id")]
    [JsonConverter(typeof(GsiFlexibleStringJsonConverter))]
    public string? ServerSteamId { get; set; }

    [JsonPropertyName("players")]
    public List<OpenDotaLivePlayer>? Players { get; set; }
}

public sealed class OpenDotaLivePlayer
{
    [JsonPropertyName("account_id")]
    public ulong AccountId { get; set; }

    [JsonPropertyName("hero_id")]
    public int HeroId { get; set; }

    [JsonPropertyName("team_slot")]
    public int TeamSlot { get; set; }

    [JsonPropertyName("team")]
    public int Team { get; set; }
}

public sealed class OpenDotaRecentMatch
{
    [JsonPropertyName("match_id")]
    public long MatchId { get; set; }

    [JsonPropertyName("player_slot")]
    public int PlayerSlot { get; set; }

    [JsonPropertyName("radiant_win")]
    public bool RadiantWin { get; set; }

    [JsonPropertyName("duration")]
    public int Duration { get; set; }

    [JsonPropertyName("hero_id")]
    public int HeroId { get; set; }

    [JsonPropertyName("lobby_type")]
    public int LobbyType { get; set; }

    [JsonPropertyName("party_size")]
    public int? PartySize { get; set; }

    /// <summary>Average rank tier of the lobby, as reported by Valve.</summary>
    [JsonPropertyName("average_rank")]
    public int? AverageRank { get; set; }

    [JsonPropertyName("start_time")]
    public long StartTime { get; set; }

    [JsonPropertyName("kills")]
    public int Kills { get; set; }

    [JsonPropertyName("deaths")]
    public int Deaths { get; set; }

    [JsonPropertyName("assists")]
    public int Assists { get; set; }
}

public sealed class OpenDotaMatchDetail
{
    [JsonPropertyName("players")]
    public List<OpenDotaMatchPlayer>? Players { get; set; }
}

public sealed class OpenDotaMatchPlayer
{
    [JsonPropertyName("player_slot")]
    public int PlayerSlot { get; set; }

    [JsonPropertyName("rank_tier")]
    public int? RankTier { get; set; }
}

public sealed class PlayerOverview
{
    public string SteamId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int? RankTier { get; set; }
    public int? LeaderboardRank { get; set; }
    public int? MmrEstimate { get; set; }
    public RankEstimate? HistoryRankEstimate { get; set; }
    public int? DisplayRankTier { get; set; }
    public bool RankIsEstimated { get; set; }
    public string RankLabel { get; set; } = "—";
    public int Wins { get; set; }
    public int Losses { get; set; }
    public double? WinRatePercent { get; set; }
    public bool IsPrivate { get; set; }
    public IReadOnlyList<PlayerMatchRow> RecentMatches { get; set; } = Array.Empty<PlayerMatchRow>();
}

public sealed class PlayerMatchRow
{
    public string MatchId { get; set; } = string.Empty;
    public string HeroName { get; set; } = string.Empty;
    public string Result { get; set; } = string.Empty;
    public string Kda { get; set; } = string.Empty;
    public string Duration { get; set; } = string.Empty;
    public string PlayedAt { get; set; } = string.Empty;
}

public sealed class PlayerPickerItem
{
    public string SteamId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string DisplayLabel =>
        string.IsNullOrWhiteSpace(Name) ? SteamId : $"{Name} · {SteamId}";
}
