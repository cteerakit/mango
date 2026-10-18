using Mango.Models;
using Mango.Models.Gsi;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using System.Text.Json;

namespace Mango.Services;

public sealed partial class MatchStore : ObservableObject
{
    private static readonly TimeSpan MinPublishInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MinRosterFetchInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FailedRosterFetchInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RosterFetchTimeout = TimeSpan.FromSeconds(25);

    private DateTimeOffset _lastPublishUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastRosterFetchUtc = DateTimeOffset.MinValue;
    private string _lastRosterFetchKey = string.Empty;
    private string? _rosterFetchInFlightKey;
    private bool _lastRosterFetchFailed;
    private int _rosterFetchGeneration;
    private CancellationTokenSource? _rosterFetchCts;
    private DispatcherQueue? _dispatcherQueue;
    private Func<MatchSnapshot, CancellationToken, Task<MatchRosterResolution?>>? _rosterResolver;
    private string _lastRosterFetchMessage = string.Empty;
    private readonly MatchPageState _matchPageState = new();
    private GsiPayload? _latestLivePayload;
    private string _lastRosterOrderKey = string.Empty;

    [ObservableProperty]
    private MatchSnapshot _snapshot = new();

    [ObservableProperty]
    private bool _isGsiConnected;

    [ObservableProperty]
    private bool _isUsingMockData;

    [ObservableProperty]
    private string _connectionStatus = "Waiting for Dota 2";

    [ObservableProperty]
    private DateTimeOffset? _lastPayloadUtc;

    public bool LastRosterFetchFailed => _lastRosterFetchFailed;

    public string LastRosterFetchMessage => _lastRosterFetchMessage;
    public string LastRosterSource { get; private set; } = string.Empty;
    public MatchSnapshot MatchPageSnapshot => IsUsingMockData ? Snapshot : _matchPageState.Snapshot;
    public bool IsShowingPreviousMatch => !IsUsingMockData && _matchPageState.IsPreviousMatch;

    public event Action<MatchSnapshot>? SnapshotUpdated;

    public void Configure(
        DispatcherQueue dispatcherQueue,
        Func<MatchSnapshot, CancellationToken, Task<MatchRosterResolution?>> rosterResolver)
    {
        _dispatcherQueue = dispatcherQueue;
        _rosterResolver = rosterResolver;
    }

    public void ApplyPayload(GsiPayload payload)
    {
        _latestLivePayload = payload;
        if (IsUsingMockData)
        {
            return;
        }

        var snapshot = RetainRicherRoster(GsiNormalizer.Normalize(payload));
        var connectionStatus = snapshot.IsInMatch
            ? payload.Player?.IsSpectating == true || payload.Hero?.IsSpectating == true
                ? "Live match — Spectator"
                : "Live match — Playing"
            : "Connected (menu/lobby)";
        PublishThrottled(snapshot, connectionStatus, connected: true);
        QueueExternalRosterFetch(snapshot);
    }

    public void ApplyMockData()
    {
        if (IsUsingMockData)
        {
            return;
        }

        CancelRosterFetch();
        IsUsingMockData = true;
        var snapshot = MockMatchData.CreateSnapshot();
        snapshot.MatchId = "debug-" + Guid.NewGuid().ToString("N");
        Publish(snapshot, "Mock data", connected: false);
    }

    public void ClearMockData()
    {
        if (!IsUsingMockData)
        {
            return;
        }

        IsUsingMockData = false;
        var latestPayload = _latestLivePayload;
        MarkDisconnected();
        if (latestPayload is not null)
        {
            ApplyPayload(latestPayload);
        }
    }

    public void MarkDisconnected()
    {
        if (IsUsingMockData)
        {
            return;
        }

        _latestLivePayload = null;
        CancelRosterFetch();
        IsGsiConnected = false;
        IsUsingMockData = false;
        ConnectionStatus = "Waiting for Dota 2";
        Snapshot = new MatchSnapshot { IsConnected = false };
        _matchPageState.Update(Snapshot);
        LastPayloadUtc = null;
        _lastPublishUtc = DateTimeOffset.MinValue;
        _lastRosterFetchKey = string.Empty;
        _lastRosterFetchUtc = DateTimeOffset.MinValue;
        _rosterFetchInFlightKey = null;
        _lastRosterFetchFailed = false;
        _lastRosterFetchMessage = string.Empty;
        LastRosterSource = string.Empty;
        SnapshotUpdated?.Invoke(Snapshot);
    }

    private void QueueExternalRosterFetch(MatchSnapshot snapshot)
    {
        if (_rosterResolver is null || _dispatcherQueue is null || IsUsingMockData || !snapshot.IsConnected)
        {
            return;
        }

        if (!MatchRosterMerger.NeedsRefresh(snapshot.Players))
        {
            return;
        }

        var localAccountId = SteamIdConverter.ToAccountId(snapshot.SteamId);
        if (!PlayerNotesMatch.IsUsableMatchId(snapshot.MatchId)
            && localAccountId is null
            && string.IsNullOrWhiteSpace(snapshot.ServerSteamId))
        {
            return;
        }

        var fetchKey = $"{snapshot.MatchId}|{snapshot.ServerSteamId}|{localAccountId}";
        var now = DateTimeOffset.UtcNow;
        if (string.Equals(fetchKey, _rosterFetchInFlightKey, StringComparison.Ordinal))
        {
            return;
        }

        // Steam's realtime stats gain teams shortly before the horn; poll faster until they arrive.
        var awaitingTeams = !string.IsNullOrWhiteSpace(snapshot.ServerSteamId)
            && MatchRosterMerger.CountTeamed(snapshot.Players) < 10;
        var retryInterval = _lastRosterFetchFailed || awaitingTeams
            ? FailedRosterFetchInterval
            : MinRosterFetchInterval;
        if (string.Equals(fetchKey, _lastRosterFetchKey, StringComparison.Ordinal)
            && now - _lastRosterFetchUtc < retryInterval)
        {
            return;
        }

        CancelRosterFetch();
        var timeoutCts = new CancellationTokenSource(RosterFetchTimeout);
        _rosterFetchCts = timeoutCts;
        var token = timeoutCts.Token;
        var fetchGeneration = _rosterFetchGeneration;
        var matchSnapshot = snapshot;
        var gsiPlayers = snapshot.Players;
        _lastRosterFetchKey = fetchKey;
        _lastRosterFetchUtc = DateTimeOffset.UtcNow;
        _rosterFetchInFlightKey = fetchKey;

        _ = Task.Run(async () =>
        {
            try
            {
                var resolution = await _rosterResolver(matchSnapshot, token);
                var external = resolution?.Players;
                LogRosterOrder(matchSnapshot, resolution);
                if (external is null
                    || (MatchRosterMerger.CountIdentified(external) <= MatchRosterMerger.CountIdentified(gsiPlayers)
                        && MatchRosterMerger.CountHeroes(external) <= MatchRosterMerger.CountHeroes(gsiPlayers)
                        && MatchRosterMerger.CountTeamed(external) <= MatchRosterMerger.CountTeamed(gsiPlayers)))
                {
                    _lastRosterFetchFailed = true;
                    _lastRosterFetchMessage = string.IsNullOrWhiteSpace(resolution?.ServerSteamId)
                        ? "This game is not in Steam's public live list, so the other players stay hidden until the match is parsed."
                        : "Found the live server, but Steam did not return the other players yet.";
                    PersistServerSteamId(matchSnapshot, resolution?.ServerSteamId);
                    NotifySnapshot();
                    return;
                }

                _lastRosterFetchFailed = false;
                _lastRosterFetchMessage = string.Empty;
                var merged = MatchRosterMerger.Merge(gsiPlayers, external);
                token.ThrowIfCancellationRequested();

                _dispatcherQueue.TryEnqueue(() =>
                {
                    if (token.IsCancellationRequested || IsUsingMockData || !Snapshot.IsConnected)
                    {
                        return;
                    }

                    if (PlayerNotesMatch.IsUsableMatchId(matchSnapshot.MatchId)
                        && !string.Equals(Snapshot.MatchId, matchSnapshot.MatchId, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    if (MatchRosterMerger.CountIdentified(merged) <= MatchRosterMerger.CountIdentified(Snapshot.Players)
                        && MatchRosterMerger.CountHeroes(merged) <= MatchRosterMerger.CountHeroes(Snapshot.Players)
                        && MatchRosterMerger.CountTeamed(merged) <= MatchRosterMerger.CountTeamed(Snapshot.Players)
                        && string.IsNullOrWhiteSpace(resolution?.ServerSteamId))
                    {
                        return;
                    }

                    var updated = CloneWithPlayers(Snapshot, MatchRosterMerger.Merge(merged, Snapshot.Players));
                    if (!string.IsNullOrWhiteSpace(resolution?.ServerSteamId))
                    {
                        updated.ServerSteamId = resolution.ServerSteamId;
                    }

                    LastRosterSource = resolution?.Source ?? string.Empty;
                    Publish(updated, ConnectionStatus, connected: true);
                });
            }
            catch (OperationCanceledException)
            {
                if (fetchGeneration == _rosterFetchGeneration && !IsUsingMockData && Snapshot.IsConnected)
                {
                    _lastRosterFetchFailed = true;
                    _lastRosterFetchMessage = "Lobby lookup timed out — retrying.";
                    NotifySnapshot();
                }
            }
            catch
            {
                _lastRosterFetchFailed = true;
                _lastRosterFetchMessage = "Lobby lookup failed — retrying.";
                NotifySnapshot();
            }
            finally
            {
                if (string.Equals(_rosterFetchInFlightKey, fetchKey, StringComparison.Ordinal))
                {
                    _rosterFetchInFlightKey = null;
                }
            }
        }, token);
    }

    /// <summary>
    /// Diagnostics only: records each source's player order so the local Steam list order can be
    /// compared with the teams Steam's realtime stats report later in the same match.
    /// </summary>
    private void LogRosterOrder(MatchSnapshot snapshot, MatchRosterResolution? resolution)
    {
        if (resolution?.Players is not { Count: > 0 } players)
        {
            return;
        }

        var key = $"{snapshot.MatchId}|{resolution.Source}|{string.Join(',', players.Select(static player => player.SteamId))}";
        if (key == _lastRosterOrderKey)
        {
            return;
        }

        _lastRosterOrderKey = key;
        try
        {
            Directory.CreateDirectory(AppSettings.SettingsDirectory);
            var line = JsonSerializer.Serialize(new
            {
                atUtc = DateTimeOffset.UtcNow,
                matchId = snapshot.MatchId,
                gameState = snapshot.GameState,
                localSteamId = snapshot.SteamId,
                localTeam = snapshot.TeamName,
                source = resolution.Source,
                players = players.Select(static player => new { player.SteamId, player.TeamName, player.TeamSlot }),
            });
            File.AppendAllText(Path.Combine(AppSettings.SettingsDirectory, "roster-order-log.jsonl"), line + Environment.NewLine);
        }
        catch
        {
            // Diagnostics only.
        }
    }

    private void PersistServerSteamId(MatchSnapshot matchSnapshot, string? serverSteamId)
    {
        if (string.IsNullOrWhiteSpace(serverSteamId) || _dispatcherQueue is null)
        {
            return;
        }

        _dispatcherQueue.TryEnqueue(() =>
        {
            if (IsUsingMockData || !Snapshot.IsConnected)
            {
                return;
            }

            if (PlayerNotesMatch.IsUsableMatchId(matchSnapshot.MatchId)
                && !string.Equals(Snapshot.MatchId, matchSnapshot.MatchId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (string.Equals(Snapshot.ServerSteamId, serverSteamId, StringComparison.Ordinal))
            {
                return;
            }

            var updated = CloneWithPlayers(Snapshot, Snapshot.Players);
            updated.ServerSteamId = serverSteamId;
            Publish(updated, ConnectionStatus, connected: true);
        });
    }

    private static MatchSnapshot CloneWithPlayers(MatchSnapshot source, IReadOnlyList<MatchPlayer> players) =>
        new()
        {
            ReceivedAtUtc = source.ReceivedAtUtc,
            IsConnected = source.IsConnected,
            GameState = source.GameState,
            ClockTimeSeconds = source.ClockTimeSeconds,
            GameTimeSeconds = source.GameTimeSeconds,
            IsDaytime = source.IsDaytime,
            MatchId = source.MatchId,
            ServerSteamId = source.ServerSteamId,
            RadiantScore = source.RadiantScore,
            DireScore = source.DireScore,
            NetWorthLead = source.NetWorthLead,
            PlayerName = source.PlayerName,
            SteamId = source.SteamId,
            Kills = source.Kills,
            Deaths = source.Deaths,
            Assists = source.Assists,
            LastHits = source.LastHits,
            Denies = source.Denies,
            Gpm = source.Gpm,
            Xpm = source.Xpm,
            Gold = source.Gold,
            TeamName = source.TeamName,
            HeroName = source.HeroName,
            HeroLevel = source.HeroLevel,
            Health = source.Health,
            MaxHealth = source.MaxHealth,
            Mana = source.Mana,
            MaxMana = source.MaxMana,
            HeroAlive = source.HeroAlive,
            Items = source.Items,
            Abilities = source.Abilities,
            Draft = source.Draft,
            Players = players,
        };

    private void CancelRosterFetch()
    {
        if (_rosterFetchCts is null)
        {
            return;
        }

        _rosterFetchCts.Cancel();
        _rosterFetchCts.Dispose();
        _rosterFetchCts = null;
        _rosterFetchInFlightKey = null;
        _rosterFetchGeneration++;
    }

    private void NotifySnapshot()
    {
        _dispatcherQueue?.TryEnqueue(() =>
        {
            if (IsUsingMockData || !Snapshot.IsConnected)
            {
                return;
            }

            SnapshotUpdated?.Invoke(Snapshot);
        });
    }

    private void PublishThrottled(MatchSnapshot snapshot, string connectionStatus, bool connected)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastPublishUtc < MinPublishInterval)
        {
            return;
        }

        Publish(snapshot, connectionStatus, connected);
    }

    private MatchSnapshot RetainRicherRoster(MatchSnapshot incoming)
    {
        var current = Snapshot;
        if (!current.IsConnected || !incoming.IsInMatch)
        {
            return incoming;
        }

        incoming.Players = MatchRosterMerger.RetainIfRicher(
            incoming.Players,
            current.Players,
            incoming.MatchId,
            current.MatchId);

        if (string.IsNullOrWhiteSpace(incoming.ServerSteamId)
            && PlayerNotesMatch.IsUsableMatchId(incoming.MatchId)
            && string.Equals(incoming.MatchId, current.MatchId, StringComparison.Ordinal))
        {
            incoming.ServerSteamId = current.ServerSteamId;
        }

        return incoming;
    }

    private void Publish(MatchSnapshot snapshot, string connectionStatus, bool connected)
    {
        _lastPublishUtc = DateTimeOffset.UtcNow;
        Snapshot = snapshot;
        if (!IsUsingMockData)
        {
            _matchPageState.Update(snapshot);
        }
        IsGsiConnected = connected;
        ConnectionStatus = connectionStatus;
        LastPayloadUtc = snapshot.ReceivedAtUtc;
        SnapshotUpdated?.Invoke(snapshot);
    }
}
