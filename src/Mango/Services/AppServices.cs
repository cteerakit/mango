using Mango.Models;

namespace Mango.Services;

public static class AppServices
{
    private static LocalSteamRosterService _localSteamRoster = new([]);
    public static AppSettings Settings { get; private set; } = AppSettings.Load();
    public static MatchStore MatchStore { get; private set; } = new();
    public static GsiListenerService GsiListener { get; private set; } = null!;
    public static GsiConfigInstaller GsiInstaller { get; private set; } = new();
    public static OpenDotaService OpenDota { get; private set; } = new(Path.Combine(AppSettings.SettingsDirectory, "cache"));
    public static SteamWebApiService SteamApi { get; private set; } = new();
    public static StratzService Stratz { get; private set; } = new(Path.Combine(AppSettings.SettingsDirectory, "cache"));
    public static TimerService TimerService { get; private set; } = null!;
    public static OverlayWindowService OverlayService { get; private set; } = null!;
    public static HotkeyService HotkeyService { get; private set; } = null!;
    public static HudFontService HudFonts { get; private set; } = new();

    public static void Initialize(Microsoft.UI.Dispatching.DispatcherQueue dispatcherQueue)
    {
        Settings = AppSettings.Load();
        OpenDota = new OpenDotaService(Path.Combine(AppSettings.SettingsDirectory, "cache"));
        SteamApi = new SteamWebApiService();
        Stratz = new StratzService(Path.Combine(AppSettings.SettingsDirectory, "cache"));
        _localSteamRoster = new LocalSteamRosterService(GsiConfigInstaller.FindDotaInstallPaths());
        MatchStore = new MatchStore();
        MatchStore.Configure(dispatcherQueue, ResolveMatchRosterAsync);
        HudFonts = new HudFontService();
        HudFonts.Initialize();
        GsiListener = new GsiListenerService(MatchStore, Settings);
        TimerService = new TimerService(MatchStore, Settings, dispatcherQueue);
        OverlayService = new OverlayWindowService(Settings);
        HotkeyService = new HotkeyService(OverlayService, Settings, dispatcherQueue);
        GsiListener.Start();

        // Never restore a blocking overlay across sessions; user enables it explicitly.
        Settings.OverlayVisible = false;
        Settings.OverlayInteractive = false;
        Settings.Save();

        if (Settings.ShowMockData)
        {
            MatchStore.ApplyMockData();
        }
    }

    public static void Shutdown()
    {
        OverlayService.Close();
        TimerService.Dispose();
        GsiListener.Stop();
        HotkeyService.Dispose();
    }

    public static void ApplyMockDataMode()
    {
        if (Settings.ShowMockData)
        {
            MatchStore.ApplyMockData();
            return;
        }

        MatchStore.ClearMockData();
    }

    public static GsiInstallResult EnsureGsiConfigInstalled() => GsiInstaller.Install(Settings);

    private static async Task<MatchRosterResolution?> ResolveMatchRosterAsync(
        MatchSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<int, string> heroNames = new Dictionary<int, string>();
        try
        {
            using var heroCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            heroCts.CancelAfter(TimeSpan.FromSeconds(2));
            heroNames = (await OpenDota.GetHeroesAsync(heroCts.Token))
                .GroupBy(static hero => hero.Id)
                .ToDictionary(static group => group.Key, static group => group.First().Name);
        }
        catch
        {
            // Hero icons can stay empty; roster identities still load.
        }

        var localAccountId = SteamIdConverter.ToAccountId(snapshot.SteamId);
        var sources = new List<Task<MatchRosterResolution?>>
        {
            _localSteamRoster.GetRosterAsync(snapshot, cancellationToken),
            ResolveFromTopLiveAsync(snapshot.MatchId, localAccountId, heroNames, cancellationToken),
            ResolveFromOpenDotaLiveAsync(snapshot.MatchId, localAccountId, cancellationToken),
        };

        if (!string.IsNullOrWhiteSpace(snapshot.ServerSteamId))
        {
            sources.Add(ResolveFromRealtimeAsync(snapshot.ServerSteamId, snapshot.MatchId, heroNames, cancellationToken));
        }

        return await FirstRichRosterAsync(sources, cancellationToken);
    }

    private static async Task<MatchRosterResolution?> ResolveFromTopLiveAsync(
        string matchId,
        ulong? localAccountId,
        IReadOnlyDictionary<int, string> heroNames,
        CancellationToken cancellationToken)
    {
        var hit = await SteamApi.FindTopLiveGameAsync(
            localAccountId,
            matchId,
            Settings.SteamApiKey,
            heroNames,
            cancellationToken);
        if (hit is null)
        {
            return null;
        }

        return ToResolution(hit.Players, hit.ServerSteamId, "steam-top-live");
    }

    private static async Task<MatchRosterResolution?> ResolveFromRealtimeAsync(
        string serverSteamId,
        string matchId,
        IReadOnlyDictionary<int, string> heroNames,
        CancellationToken cancellationToken)
    {
        var players = await SteamApi.GetRealtimeRosterAsync(
            serverSteamId,
            Settings.SteamApiKey,
            heroNames,
            cancellationToken,
            expectedMatchId: matchId);
        return ToResolution(players, serverSteamId, "steam-realtime");
    }

    private static async Task<MatchRosterResolution?> ResolveFromOpenDotaLiveAsync(
        string matchId,
        ulong? localAccountId,
        CancellationToken cancellationToken)
    {
        var players = await OpenDota.GetMatchRosterAsync(matchId, localAccountId, cancellationToken);
        return ToResolution(players, null, "opendota-live");
    }

    private static async Task<MatchRosterResolution?> FirstRichRosterAsync(
        IReadOnlyList<Task<MatchRosterResolution?>> sources,
        CancellationToken cancellationToken)
    {
        MatchRosterResolution? best = null;
        var remaining = sources.ToList();
        while (remaining.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task<MatchRosterResolution?> done;
            try
            {
                var next = Task.WhenAny(remaining);
                done = best is null
                    ? await next
                    : await next.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken);
            }
            catch (TimeoutException)
            {
                return best;
            }
            remaining.Remove(done);

            MatchRosterResolution? result;
            try
            {
                result = await done;
            }
            catch (OperationCanceledException)
            {
                continue;
            }
            catch
            {
                continue;
            }

            if (result is null || (result.IdentifiedCount == 0 && string.IsNullOrWhiteSpace(result.ServerSteamId)))
            {
                continue;
            }

            if (best is null || result.IdentifiedCount > best.IdentifiedCount
                || (result.IdentifiedCount == best.IdentifiedCount
                    && MatchRosterMerger.CountTeamed(result.Players ?? []) > MatchRosterMerger.CountTeamed(best.Players ?? []))
                || (result.IdentifiedCount == best.IdentifiedCount
                    && MatchRosterMerger.CountTeamed(result.Players ?? []) == MatchRosterMerger.CountTeamed(best.Players ?? [])
                    && MatchRosterMerger.CountHeroes(result.Players ?? []) > MatchRosterMerger.CountHeroes(best.Players ?? [])))
            {
                best = result;
            }

            if (!MatchRosterMerger.NeedsRefresh(best.Players ?? []))
            {
                return best;
            }
        }

        return best;
    }

    private static MatchRosterResolution? ToResolution(
        IReadOnlyList<MatchPlayer>? players,
        string? serverSteamId,
        string source)
    {
        if (players is not { Count: > 0 } && string.IsNullOrWhiteSpace(serverSteamId))
        {
            return null;
        }

        return new MatchRosterResolution
        {
            Players = players,
            ServerSteamId = serverSteamId,
            Source = source,
        };
    }
}
