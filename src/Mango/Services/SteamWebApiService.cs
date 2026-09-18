using System.Text.Json;
using System.Text.Json.Serialization;
using Mango.Models;

namespace Mango.Services;

public sealed class SteamWebApiService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    private readonly Dictionary<string, CachedAvatar> _avatarCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan AvatarCacheDuration = TimeSpan.FromHours(12);

    public SteamWebApiService()
    {
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mango/1.0");
    }

    public async Task<IReadOnlyDictionary<string, SteamPlayerSummary>> GetPlayerSummariesAsync(
        IEnumerable<string> playerIds,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, SteamPlayerSummary>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return result;
        }

        var steamId64ByInput = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pendingSteamIds = new List<string>();

        foreach (var playerId in playerIds
                     .Select(static id => id?.Trim() ?? string.Empty)
                     .Where(static id => !string.IsNullOrWhiteSpace(id))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (_avatarCache.TryGetValue(playerId, out var cached)
                && DateTimeOffset.UtcNow - cached.CachedAtUtc < AvatarCacheDuration)
            {
                result[playerId] = cached.Summary;
                continue;
            }

            var steamId64 = SteamIdConverter.ToSteamId64(playerId);
            if (steamId64 is null)
            {
                continue;
            }

            var steamIdText = steamId64.Value.ToString();
            steamId64ByInput[playerId] = steamIdText;
            if (!pendingSteamIds.Contains(steamIdText, StringComparer.Ordinal))
            {
                pendingSteamIds.Add(steamIdText);
            }
        }

        if (pendingSteamIds.Count == 0)
        {
            return result;
        }

        // Steam allows up to 100 IDs per GetPlayerSummaries call.
        foreach (var batch in pendingSteamIds.Chunk(100))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url =
                "https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/"
                + $"?key={Uri.EscapeDataString(apiKey.Trim())}"
                + $"&steamids={string.Join(',', batch)}";

            using var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                continue;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<SteamSummariesResponse>(stream, JsonOptions, cancellationToken);
            var players = payload?.Response?.Players ?? [];
            var bySteamId = players
                .Where(static player => !string.IsNullOrWhiteSpace(player.SteamId))
                .ToDictionary(static player => player.SteamId, StringComparer.Ordinal);

            foreach (var (inputId, steamId64) in steamId64ByInput)
            {
                if (!batch.Contains(steamId64, StringComparer.Ordinal)
                    || !bySteamId.TryGetValue(steamId64, out var summaryDto))
                {
                    continue;
                }

                var summary = new SteamPlayerSummary
                {
                    SteamId64 = steamId64,
                    PersonaName = summaryDto.PersonaName ?? string.Empty,
                    AvatarUrl = FirstNonEmpty(summaryDto.AvatarFull, summaryDto.AvatarMedium, summaryDto.Avatar),
                    IsPrivate = summaryDto.CommunityVisibilityState is > 0 and < 3,
                };

                _avatarCache[inputId] = new CachedAvatar(summary, DateTimeOffset.UtcNow);
                result[inputId] = summary;
            }
        }

        return result;
    }

    public async Task<IReadOnlyList<MatchPlayer>?> GetRealtimeRosterAsync(
        string? serverSteamId,
        string apiKey,
        IReadOnlyDictionary<int, string>? heroNames = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serverSteamId) || string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        var url =
            "https://api.steampowered.com/IDOTA2MatchStats_570/GetRealtimeStats/v1/"
            + $"?key={Uri.EscapeDataString(apiKey.Trim())}"
            + $"&server_steam_id={Uri.EscapeDataString(serverSteamId.Trim())}";

        using var response = await _httpClient.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<SteamRealtimeStatsResponse>(
            stream,
            JsonOptions,
            cancellationToken);

        var teams = payload?.Result?.Teams;
        if (teams is not { Count: > 0 })
        {
            return null;
        }

        var players = new List<MatchPlayer>();
        foreach (var team in teams)
        {
            var teamName = team.TeamNumber == 3 ? "dire" : "radiant";
            var teamPlayers = team.Players ?? [];
            for (var index = 0; index < teamPlayers.Count; index++)
            {
                var player = teamPlayers[index];
                var accountId = player.AccountId;
                string? heroName = null;
                heroNames?.TryGetValue(player.HeroId, out heroName);
                players.Add(new MatchPlayer
                {
                    SteamId = accountId > 0 ? accountId.ToString() : string.Empty,
                    Name = FirstNonEmpty(player.Name, accountId > 0 ? accountId.ToString() : "Unknown"),
                    TeamName = teamName,
                    TeamSlot = index,
                    HeroName = heroName ?? string.Empty,
                    HeroLevel = player.Level,
                    Kills = player.Kills,
                    Deaths = player.Deaths,
                    Assists = player.Assists,
                    LastHits = player.LastHits,
                    Denies = player.Denies,
                    Gold = player.Gold,
                });
            }
        }

        return players.Count > 0 ? players : null;
    }

    public async Task<SteamTopLiveHit?> FindTopLiveGameAsync(
        ulong? accountId,
        string? matchId,
        string apiKey,
        IReadOnlyDictionary<int, string>? heroNames = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey)
            || (accountId is null && !PlayerNotesMatch.IsUsableMatchId(matchId)))
        {
            return null;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var partnerTasks = Enumerable.Range(0, 11)
            .Select(partner => FetchTopLivePartnerAsync(partner, apiKey, linked.Token))
            .ToList();

        while (partnerTasks.Count > 0)
        {
            var done = await Task.WhenAny(partnerTasks);
            partnerTasks.Remove(done);

            SteamTopLiveResponse? payload;
            try
            {
                payload = await done;
            }
            catch (OperationCanceledException)
            {
                continue;
            }
            catch
            {
                continue;
            }

            var hit = SteamTopLiveMapper.FindGame(payload, accountId, matchId, heroNames);
            if (hit is null)
            {
                continue;
            }

            linked.Cancel();
            return hit;
        }

        return null;
    }

    private async Task<SteamTopLiveResponse?> FetchTopLivePartnerAsync(
        int partner,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var url =
            "https://api.steampowered.com/IDOTA2Match_570/GetTopLiveGame/v1/"
            + $"?key={Uri.EscapeDataString(apiKey.Trim())}"
            + $"&partner={partner}";

        using var response = await _httpClient.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<SteamTopLiveResponse>(
            stream,
            SteamTopLiveMapper.JsonOptions,
            cancellationToken);
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

    private sealed record CachedAvatar(SteamPlayerSummary Summary, DateTimeOffset CachedAtUtc);

    private sealed class SteamSummariesResponse
    {
        [JsonPropertyName("response")]
        public SteamSummariesBody? Response { get; set; }
    }

    private sealed class SteamSummariesBody
    {
        [JsonPropertyName("players")]
        public List<SteamSummaryDto> Players { get; set; } = [];
    }

    private sealed class SteamSummaryDto
    {
        [JsonPropertyName("steamid")]
        public string SteamId { get; set; } = string.Empty;

        [JsonPropertyName("personaname")]
        public string? PersonaName { get; set; }

        [JsonPropertyName("avatar")]
        public string? Avatar { get; set; }

        [JsonPropertyName("avatarmedium")]
        public string? AvatarMedium { get; set; }

        [JsonPropertyName("avatarfull")]
        public string? AvatarFull { get; set; }

        /// <summary>1 = private, 2 = friends only, 3 = public.</summary>
        [JsonPropertyName("communityvisibilitystate")]
        public int CommunityVisibilityState { get; set; }
    }

    private sealed class SteamRealtimeStatsResponse
    {
        [JsonPropertyName("result")]
        public SteamRealtimeStatsResult? Result { get; set; }
    }

    private sealed class SteamRealtimeStatsResult
    {
        [JsonPropertyName("teams")]
        public List<SteamRealtimeTeam>? Teams { get; set; }
    }

    private sealed class SteamRealtimeTeam
    {
        [JsonPropertyName("team_number")]
        public int TeamNumber { get; set; }

        [JsonPropertyName("players")]
        public List<SteamRealtimePlayer>? Players { get; set; }
    }

    private sealed class SteamRealtimePlayer
    {
        [JsonPropertyName("accountid")]
        public ulong AccountId { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("heroid")]
        public int HeroId { get; set; }

        [JsonPropertyName("level")]
        public int Level { get; set; }

        [JsonPropertyName("kill_count")]
        public int Kills { get; set; }

        [JsonPropertyName("death_count")]
        public int Deaths { get; set; }

        [JsonPropertyName("assists_count")]
        public int Assists { get; set; }

        [JsonPropertyName("lh_count")]
        public int LastHits { get; set; }

        [JsonPropertyName("denies_count")]
        public int Denies { get; set; }

        [JsonPropertyName("gold")]
        public int Gold { get; set; }
    }
}

public sealed class SteamPlayerSummary
{
    public string SteamId64 { get; init; } = string.Empty;
    public string PersonaName { get; init; } = string.Empty;
    public string AvatarUrl { get; init; } = string.Empty;
    public bool IsPrivate { get; init; }
}
