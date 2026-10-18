using Mango.Models;

namespace Mango.Services;

public sealed class MatchHistoryLoader(OpenDotaService openDota)
{
    public async Task<IReadOnlyDictionary<string, PlayerMatchHistory>> LoadAsync(
        IReadOnlyList<(string Key, string SteamId, string Hero)> requests,
        string localSteamId, string matchId, CancellationToken cancellationToken,
        Func<long, CancellationToken, Task<IReadOnlyDictionary<long, int>?>>? matchPositions = null,
        Func<long, long, CancellationToken, Task<IReadOnlyList<StratzItemPurchase>?>>? matchPurchases = null)
    {
        using var throttle = new SemaphoreSlim(3);
        var results = await Task.WhenAll(requests.Select(async request =>
        {
            await throttle.WaitAsync(cancellationToken);
            try
            {
                var recentTask = WithFallbackAsync(
                    () => openDota.GetRecentMatchSummariesAsync(request.SteamId, matchId, cancellationToken),
                    (Matches: (IReadOnlyList<RecentMatchSummary>)[], Label: "Recent matches unavailable."));
                var encounterTask = WithFallbackAsync(
                    () => openDota.GetEncounterLabelAsync(localSteamId, request.SteamId, matchId, cancellationToken),
                    "Encounter history unavailable.");
                var buildTask = WithFallbackAsync(
                    () => openDota.GetLatestHeroBuildAsync(request.SteamId, request.Hero, matchId,
                        cancellationToken, matchPositions, matchPurchases),
                    new PlayerMatchHistory { HeroMatchLabel = "Hero match history unavailable." });
                await Task.WhenAll(recentTask, encounterTask, buildTask);
                cancellationToken.ThrowIfCancellationRequested();
                var recent = await recentTask;
                var build = await buildTask;
                return (request.Key, History: new PlayerMatchHistory
                {
                    EncounterLabel = await encounterTask,
                    Kda = build.Kda,
                    Result = build.Result,
                    Position = build.Position,
                    PositionBasis = build.PositionBasis,
                    IsPositionEstimated = build.IsPositionEstimated,
                    PlayedAt = build.PlayedAt,
                    HeroMatchLabel = build.HeroMatchLabel,
                    HeroMatchUrl = build.HeroMatchUrl,
                    Items = build.Items,
                    RecentMatches = recent.Matches,
                    RecentMatchesLabel = recent.Label,
                });
            }
            finally
            {
                throttle.Release();
            }
        }));
        cancellationToken.ThrowIfCancellationRequested();
        return results.ToDictionary(result => result.Key, result => result.History);
    }

    private static async Task<T> WithFallbackAsync<T>(Func<Task<T>> load, T fallback)
    {
        try { return await load(); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return fallback; }
    }
}
