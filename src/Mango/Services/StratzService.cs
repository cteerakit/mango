using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Mango.Services;

/// <summary>One item purchase: Valve item ID and seconds from match start (negative before the horn).</summary>
public readonly record struct StratzItemPurchase(int ItemId, int Time);

/// <summary>Reads per-player positions from the STRATZ GraphQL API (needs a personal API token).</summary>
public sealed class StratzService
{
    private const string PositionsQuery =
        "query($id: Long!){ match(id: $id){ players { steamAccountId position } } }";

    private const string PurchasesQuery =
        "query($id: Long!){ match(id: $id){ players { steamAccountId stats { itemPurchases { itemId time } } } } }";

    private readonly HttpClient _httpClient;
    private readonly string _cacheDirectory;

    public StratzService(string cacheDirectory, HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient
        {
            BaseAddress = new Uri("https://api.stratz.com/"),
            Timeout = TimeSpan.FromSeconds(15),
        };
        // STRATZ rejects API calls without this user agent.
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("STRATZ_API");

        _cacheDirectory = cacheDirectory;
        Directory.CreateDirectory(_cacheDirectory);
    }

    /// <summary>Returns position 1-5 by account ID, or null when STRATZ has no positions for the match.</summary>
    public async Task<IReadOnlyDictionary<long, int>?> GetMatchPositionsAsync(
        long matchId, string apiToken, CancellationToken cancellationToken = default)
    {
        if (matchId <= 0 || string.IsNullOrWhiteSpace(apiToken)) return null;

        // Positions of a finished match never change, so a cached result is kept indefinitely.
        var cachePath = Path.Combine(_cacheDirectory, $"stratz_match_{matchId}_positions.json");
        if (File.Exists(cachePath))
        {
            try
            {
                var cached = JsonSerializer.Deserialize<Dictionary<long, int>>(
                    await File.ReadAllTextAsync(cachePath, cancellationToken));
                if (cached is { Count: > 0 }) return cached;
            }
            catch (JsonException) { /* Refetch a corrupt cache file. */ }
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "graphql")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { query = PositionsQuery, variables = new { id = matchId } }),
                    Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken.Trim());
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var positions = ReadPositions(document.RootElement);
            if (positions.Count == 0) return null;
            await File.WriteAllTextAsync(cachePath, JsonSerializer.Serialize(positions), cancellationToken);
            return positions;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or JsonException
                                              or TaskCanceledException or IOException)
        {
            return null;
        }
    }

    /// <summary>Returns a player's item purchases in time order, or null when STRATZ has none for the match.</summary>
    public async Task<IReadOnlyList<StratzItemPurchase>?> GetPlayerPurchasesAsync(
        long matchId, long accountId, string apiToken, CancellationToken cancellationToken = default)
    {
        if (matchId <= 0 || accountId <= 0 || string.IsNullOrWhiteSpace(apiToken)) return null;

        var cachePath = Path.Combine(_cacheDirectory, $"stratz_match_{matchId}_purchases_{accountId}.json");
        if (File.Exists(cachePath))
        {
            try
            {
                var cached = JsonSerializer.Deserialize<List<StratzItemPurchase>>(
                    await File.ReadAllTextAsync(cachePath, cancellationToken));
                if (cached is { Count: > 0 }) return cached;
            }
            catch (JsonException) { /* Refetch a corrupt cache file. */ }
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "graphql")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { query = PurchasesQuery, variables = new { id = matchId } }),
                    Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken.Trim());
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var purchases = ReadPurchases(document.RootElement, accountId);
            if (purchases.Count == 0) return null;
            await File.WriteAllTextAsync(cachePath, JsonSerializer.Serialize(purchases), cancellationToken);
            return purchases;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or JsonException
                                              or TaskCanceledException or IOException)
        {
            return null;
        }
    }

    private static List<StratzItemPurchase> ReadPurchases(JsonElement root, long accountId)
    {
        var purchases = new List<StratzItemPurchase>();
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("match", out var match) || match.ValueKind != JsonValueKind.Object
            || !match.TryGetProperty("players", out var players) || players.ValueKind != JsonValueKind.Array)
            return purchases;

        foreach (var player in players.EnumerateArray())
        {
            if (player.ValueKind != JsonValueKind.Object
                || !player.TryGetProperty("steamAccountId", out var id) || !id.TryGetInt64(out var playerId)
                || playerId != accountId
                || !player.TryGetProperty("stats", out var stats) || stats.ValueKind != JsonValueKind.Object
                || !stats.TryGetProperty("itemPurchases", out var list) || list.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var purchase in list.EnumerateArray())
            {
                if (purchase.ValueKind == JsonValueKind.Object
                    && purchase.TryGetProperty("itemId", out var itemId) && itemId.TryGetInt32(out var item)
                    && purchase.TryGetProperty("time", out var time) && time.TryGetInt32(out var seconds))
                {
                    purchases.Add(new StratzItemPurchase(item, seconds));
                }
            }
            break;
        }
        return purchases.OrderBy(purchase => purchase.Time).ToList();
    }

    private static Dictionary<long, int> ReadPositions(JsonElement root)
    {
        var positions = new Dictionary<long, int>();
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("match", out var match) || match.ValueKind != JsonValueKind.Object
            || !match.TryGetProperty("players", out var players) || players.ValueKind != JsonValueKind.Array)
            return positions;

        foreach (var player in players.EnumerateArray())
        {
            if (player.ValueKind == JsonValueKind.Object
                && player.TryGetProperty("steamAccountId", out var id) && id.ValueKind == JsonValueKind.Number
                && id.TryGetInt64(out var accountId) && accountId > 0
                && player.TryGetProperty("position", out var position) && position.ValueKind == JsonValueKind.String
                && position.GetString() is { } value && value.StartsWith("POSITION_", StringComparison.Ordinal)
                && int.TryParse(value["POSITION_".Length..], out var number) && number is >= 1 and <= 5)
            {
                positions[accountId] = number;
            }
        }
        return positions;
    }
}
